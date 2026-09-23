using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Common;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Centros;

/// <summary>
/// <see cref="CalculoEstadoCentroService"/> ya prueba que una acreditación
/// rechazada por la plataforma genera una <see cref="CausaEstadoCentro"/>
/// bloqueante (<c>CalculoEstadoCentroServiceTests</c>). Lo que no estaba
/// probado es el segundo salto: <c>ObtenerCentrosQuery.Desglosar</c> agrupa
/// esas causas en <see cref="RecuentosCentroDto"/> por
/// <see cref="CausaEstadoCentro.Estado"/>, y esa causa concreta llevaba
/// <c>Estado: null</c> — no encajaba en ningún <c>case</c> del switch y se
/// descartaba en silencio. El síntoma real (Centro 360, D-7 del piloto
/// Outbound): la insignia "Acceso bloqueado" aparecía sin ningún recuento ni
/// texto que dijera por qué. El arreglo real está en el origen del dato
/// (<see cref="CalculoEstadoCentroService"/> ya no produce <c>Estado: null</c>
/// para esta causa, usa <see cref="EstadoDocumento.Vencido"/> como su causa
/// hermana "vencido en la plataforma") — necesario porque el Badge de Centro
/// 360 (<c>AcordeonAsignacionesCentro</c>) indexa por
/// <see cref="EstadoDocumento"/> y no admite <c>null</c>: un caso especial en
/// el switch del Query por sí solo dejaba ese Badge expuesto a la misma
/// causa cuando su ámbito es Empresa (<c>TrabajadorId</c> nulo en la fila
/// rechazada) en vez de Trabajador.
///
/// Mismo defecto para <see cref="EstadoDocumento.Urgente"/> — el switch solo
/// cubría Vencido/Faltante y Próximo, así que un documento dentro del umbral
/// rojo tampoco aparecía en ningún recuento aunque sí influye en
/// <see cref="EstadoCentro"/> (ver <see cref="CalculadoraEstadoCentro"/>).
/// </summary>
public class ObtenerCentrosQueryRecuentosTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _centroId;
    private Guid _empresaId;
    private Guid _trabajadorId;
    private Guid _tipoDocumentoObligatorioId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        // Umbral rojo = 15 días: un documento que vence dentro de ese margen
        // cae en EstadoDocumento.Urgente (ni Vencido ni Próximo).
        contexto.ParametrosSistema.Add(new ParametroSistema(30, 15));

        var cliente = Empresa.CrearComoCliente("Cliente Recuentos S.L.", "B10000032", false, null, null);
        var empresa = new Empresa("Empresa Recuentos S.L.", "B20000030");
        contexto.Empresas.AddRange(cliente, empresa);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro Recuentos");
        contexto.Centros.Add(centro);

        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Ana", "García", "77189989B");
        contexto.Trabajadores.Add(trabajador);

        var tipoObligatorio = new TipoDocumento(
            "EPIs", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador,
            requerido: RequisitoDocumental.Si);
        contexto.TiposDocumento.Add(tipoObligatorio);
        await contexto.SaveChangesAsync();

        contexto.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, DateOnly.FromDateTime(DateTime.UtcNow)));
        await contexto.SaveChangesAsync();

        _centroId = centro.Id;
        _empresaId = empresa.Id;
        _trabajadorId = trabajador.Id;
        _tipoDocumentoObligatorioId = tipoObligatorio.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Un_Centro_Bloqueado_solo_por_una_acreditacion_Rechazada_declara_la_causa_en_Vencidas()
    {
        // Documento al día en TALVEG — lo único que bloquea es el rechazo en
        // la plataforma, para aislar exactamente la causa bajo prueba.
        await SembrarDocumentoAlDiaAsync();
        await SembrarAcreditacionRechazadaAsync();

        var centro = await ObtenerCentroUnicoAsync();

        centro.Estado.Should().Be(EstadoCentro.Bloqueado,
            "control positivo: sin esto, el resto de la prueba no mediría nada");
        centro.Recuentos.TotalVencidas.Should().Be(1,
            "una causa bloqueante sin vigencia documental que describir sigue siendo una incidencia que declarar, " +
            "no un hueco silencioso entre Vencidas y Próximas");
        centro.Recuentos.Vencidas.Should().ContainSingle(i => i.Descripcion.Contains("rechazado por la plataforma"))
            .Which.Estado.Should().Be(EstadoDocumento.Vencido,
                "nunca null: el Badge de Centro 360 (AcordeonAsignacionesCentro) indexa por EstadoDocumento " +
                "y no admite null — la causa debe traer un estado real, igual que su hermana \"vencido en la plataforma\"");
    }

    [Fact]
    public async Task Un_Centro_Bloqueado_por_un_rechazo_de_documento_de_Empresa_declara_la_causa_con_Estado_no_nulo()
    {
        // Mismo defecto, camino distinto: TrabajadorId nulo en la fila
        // rechazada produce AmbitoCausa.Empresa (no Trabajador). Es el camino
        // que AcordeonAsignacionesCentro.razor renderiza con
        // "incidencia.Estado!.Value" sin comprobar null — si la causa de
        // rechazo volviera a llevar Estado: null, esto reproduciría el
        // InvalidOperationException real, no solo un hueco en el recuento.
        var tipoEmpresa = await SembrarTipoDocumentoEmpresaAsync();
        var documentoEmpresa = await SembrarDocumentoEmpresaAlDiaAsync(tipoEmpresa);
        await SembrarAcreditacionRechazadaAsync(documentoEmpresa);

        var centro = await ObtenerCentroUnicoAsync();

        centro.Estado.Should().Be(EstadoCentro.Bloqueado);
        centro.Recuentos.Vencidas.Should().ContainSingle(i => i.Descripcion.Contains("rechazado por la plataforma"))
            .Which.Should().Match<IncidenciaCentroDto>(i =>
                i.Ambito == AmbitoCausa.Empresa && i.Estado == EstadoDocumento.Vencido);
    }

    [Fact]
    public async Task Un_documento_en_estado_Urgente_aparece_en_Proximas()
    {
        // Vence dentro de 10 días: por debajo del umbral rojo (15) pero no
        // vencido — EstadoDocumento.Urgente, no Vencido ni Proximo.
        await SembrarDocumentoTrabajadorAsync(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10));

        var centro = await ObtenerCentroUnicoAsync();

        centro.Estado.Should().Be(EstadoCentro.Urgente,
            "control positivo: EstadoCentro.Urgente se deriva exactamente de esta causa (CalculadoraEstadoCentro)");
        centro.Recuentos.TotalProximas.Should().Be(1,
            "Urgente es una vigencia próxima a vencer más severa que Proximo, no una tercera casilla fuera del recuento");
        centro.Recuentos.TotalVencidas.Should().Be(0,
            "el documento aún no venció: no es correcto contarlo como si lo estuviera");
    }

    private async Task<CentroListaDto> ObtenerCentroUnicoAsync()
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerCentrosQueryHandler(
            contexto, contexto, new AlcanceDatosServiceFalso(),
            new CalculoEstadoCentroService(contexto, contexto, contexto, contexto, contexto, contexto));

        var resultado = await handler.Handle(
            new ObtenerCentrosQuery(null, null, Estado: null, CentroId: _centroId),
            CancellationToken.None);

        return resultado.Elementos.Should().ContainSingle().Which;
    }

    private async Task SembrarDocumentoAlDiaAsync()
    {
        await using var contexto = CrearContexto();
        contexto.Documentos.Add(Documento.DeTrabajador(
            _trabajadorId, _tipoDocumentoObligatorioId,
            DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1)));
        await contexto.SaveChangesAsync();
    }

    private async Task SembrarDocumentoTrabajadorAsync(DateOnly fechaVencimiento)
    {
        await using var contexto = CrearContexto();
        contexto.Documentos.Add(Documento.DeTrabajador(
            _trabajadorId, _tipoDocumentoObligatorioId,
            DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-1), fechaVencimiento));
        await contexto.SaveChangesAsync();
    }

    private async Task<Guid> SembrarTipoDocumentoEmpresaAsync()
    {
        await using var contexto = CrearContexto();
        var tipoEmpresa = new TipoDocumento(
            "Seguro RC", null, aplicaVencimientoAutomatico: false, 2, AmbitoAplicacion.Empresa,
            requerido: RequisitoDocumental.Si);
        contexto.TiposDocumento.Add(tipoEmpresa);
        await contexto.SaveChangesAsync();
        return tipoEmpresa.Id;
    }

    private async Task<Guid> SembrarDocumentoEmpresaAlDiaAsync(Guid tipoEmpresaId)
    {
        await using var contexto = CrearContexto();
        var documento = Documento.DeEmpresa(
            _empresaId, tipoEmpresaId,
            DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1));
        contexto.Documentos.Add(documento);
        await contexto.SaveChangesAsync();
        return documento.Id;
    }

    private async Task SembrarAcreditacionRechazadaAsync(Guid? documentoId = null)
    {
        await using var contexto = CrearContexto();

        var documentoIdReal = documentoId
            ?? (await contexto.Documentos.SingleAsync(d => d.TrabajadorId == _trabajadorId)).Id;

        var proveedor = new ProveedorPlataformaCae("PLAT-PRUEBA", "Plataforma de prueba");
        contexto.ProveedoresPlataformaCae.Add(proveedor);
        await contexto.SaveChangesAsync();

        var canal = CanalGestionDocumental.DePlataforma(_centroId, "Acceso de prueba", proveedor.Id, null, null, null);
        contexto.CanalesGestionDocumental.Add(canal);
        await contexto.SaveChangesAsync();

        var acreditacion = new AcreditacionDocumentoPlataforma(documentoIdReal, canal.Id);
        acreditacion.Rechazar(CausaRechazoAcreditacion.Otro, "Documento ilegible", DateTime.UtcNow);
        contexto.AcreditacionesDocumentoPlataforma.Add(acreditacion);
        await contexto.SaveChangesAsync();
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
