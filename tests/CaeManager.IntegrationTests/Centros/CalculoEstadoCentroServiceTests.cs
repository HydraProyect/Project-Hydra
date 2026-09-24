using CaeManager.Application.Centros;
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
/// Pieza 5 del backlog de gestión EPI: CalculadoraEstadoCentro (Domain) ya
/// tiene sus propios tests puros — estos verifican que
/// CalculoEstadoCentroService reúne correctamente los datos reales
/// (Documentos de Empresa/Trabajador, huecos obligatorios,
/// TipoDocumentoCentro.BloqueaAcceso) contra Postgres, incluyendo los joins
/// que un test en memoria no ejercita.
/// </summary>
public class CalculoEstadoCentroServiceTests : IAsyncLifetime
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

        if (await contexto.ParametrosSistema.SingleOrDefaultAsync() is null)
            contexto.ParametrosSistema.Add(new ParametroSistema(30, 15));

        var cliente = Empresa.CrearComoCliente("Cliente EstadoCentro S.L.", "B12345674", false, null, null);
        var empresa = new Empresa("Empresa EstadoCentro S.L.", "B87654323");
        contexto.Empresas.Add(cliente);
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro EstadoCentro");
        contexto.Centros.Add(centro);

        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Ana", "García", "77189989B");
        contexto.Trabajadores.Add(trabajador);

        var tipoObligatorio = new TipoDocumento("EPIs", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
        contexto.TiposDocumento.Add(tipoObligatorio);
        await contexto.SaveChangesAsync();

        contexto.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, DateOnly.FromDateTime(DateTime.UtcNow)));
        await contexto.SaveChangesAsync();

        _centroId = centro.Id;
        _empresaId = empresa.Id;
        _trabajadorId = trabajador.Id;
        _tipoDocumentoObligatorioId = tipoObligatorio.Id;
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // --- Vigencia en la plataforma ---------------------------------------
    //
    // Decisión del propietario (2026-09-21): un documento vencido EN LA
    // PLATAFORMA pone el Centro en rojo aunque en TALVEG siga vigente. Si el
    // portal no lo acepta, el Trabajador no entra, y el semáforo existe para
    // decir si se puede trabajar.

    [Fact]
    public async Task Vencido_en_la_plataforma_bloquea_el_Centro_aunque_el_documento_siga_vigente_en_TALVEG()
    {
        await SembrarAcreditacionAsync(
            VigenciaEnPlataforma.VenceEl(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1)));

        var resultado = await CalcularAsync();

        resultado.Estado.Should().Be(EstadoCentro.Bloqueado);
        resultado.Causas.Should().Contain(c =>
            c.Bloqueante && c.Descripcion.Contains("vencido en la plataforma"));
    }

    [Fact]
    public async Task Sin_confirmar_la_vigencia_NO_bloquea_el_Centro()
    {
        // Mira en dirección contraria a la prueba de arriba, y es la que de
        // verdad protege: al desplegar esto, TODAS las acreditaciones que ya
        // existen quedan sin confirmar. Si "no lo sé" contara como vencido, el
        // día del despliegue todos los Centros se pondrían en rojo a la vez.
        await SembrarAcreditacionAsync(VigenciaEnPlataforma.SinConfirmar);

        var resultado = await CalcularAsync();

        resultado.Estado.Should().Be(EstadoCentro.Vigente);
        resultado.Causas.Should().NotContain(c => c.Descripcion.Contains("plataforma"));
    }

    [Fact]
    public async Task Vigente_en_la_plataforma_no_anade_ninguna_causa()
    {
        // Control positivo del sembrado: sin esto, las dos pruebas de arriba
        // pasarían igual si SembrarAcreditacionAsync no escribiera nada.
        await SembrarAcreditacionAsync(
            VigenciaEnPlataforma.VenceEl(DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(6)));

        var resultado = await CalcularAsync();

        resultado.Estado.Should().Be(EstadoCentro.Vigente);
        resultado.Causas.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_trabajador_desvinculado_no_bloquea_el_Centro_por_su_vigencia_en_la_plataforma()
    {
        // Misma acreditación vencida que la primera prueba de este bloque: la
        // única diferencia es la baja de la Asignación. La acreditación no
        // muere con ella —sigue en la base de datos, vencida—, así que sin el
        // filtro por asignación activa un Trabajador que ya no pisa el Centro
        // lo bloquearía indefinidamente.
        await SembrarAcreditacionAsync(
            VigenciaEnPlataforma.VenceEl(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1)));

        await using (var contexto = CrearContexto())
        {
            var asignacion = await contexto.Asignaciones.SingleAsync(a => a.TrabajadorId == _trabajadorId);
            asignacion.DarDeBaja(DateOnly.FromDateTime(DateTime.UtcNow));
            await contexto.SaveChangesAsync();
        }

        var resultado = await CalcularAsync();

        resultado.Causas.Should().NotContain(c => c.Descripcion.Contains("vencido en la plataforma"));
    }

    // --- Acreditación rechazada por la plataforma (D-7, piloto Outbound) --------
    //
    // Una acreditación externa APLICABLE en estado Rechazada es causa bloqueante
    // del cumplimiento contextual del Centro (mismo motor, sin estado global
    // «Apto»). Aplicable = el canal de plataforma de ESE Centro, el Trabajador
    // aún asignado a él y el tipo no excluido por el Centro. Las pruebas de
    // contaminación importan tanto como la positiva: un rechazo en un Centro, un
    // Trabajador desvinculado o un tipo excluido NO deben tocar a los demás.

    private const string TextoRechazo = "rechazado por la plataforma";

    private static void Rechazada(AcreditacionDocumentoPlataforma a) =>
        a.Rechazar(CausaRechazoAcreditacion.Otro, "Documento ilegible", DateTime.UtcNow);

    [Fact]
    public async Task Vigente_en_TALVEG_y_aceptada_en_la_plataforma_deja_el_Centro_Vigente()
    {
        await SembrarAcreditacionEnAsync(_centroId, _trabajadorId, a => a.MarcarAceptada(VigenciaEnPlataforma.SinConfirmar));

        var resultado = await CalcularAsync();

        resultado.Estado.Should().Be(EstadoCentro.Vigente);
        resultado.Causas.Should().BeEmpty();
    }

    [Fact]
    public async Task Vigente_en_TALVEG_pero_rechazada_por_la_plataforma_bloquea_el_Centro()
    {
        await SembrarAcreditacionEnAsync(_centroId, _trabajadorId, Rechazada);

        var resultado = await CalcularAsync();

        resultado.Estado.Should().Be(EstadoCentro.Bloqueado);
        resultado.Causas.Should().ContainSingle(c => c.Bloqueante && c.Descripcion.Contains(TextoRechazo))
            .Which.Descripcion.Should().Contain("Ana García", "el rechazo debe decir de quién es");
    }

    [Fact]
    public async Task Vigente_en_TALVEG_y_pendiente_de_subir_no_bloquea_el_Centro()
    {
        await SembrarAcreditacionEnAsync(_centroId, _trabajadorId, _ => { });

        var resultado = await CalcularAsync();

        resultado.Estado.Should().Be(EstadoCentro.Vigente);
        resultado.Causas.Should().NotContain(c => c.Descripcion.Contains(TextoRechazo));
    }

    [Fact]
    public async Task Vigente_en_TALVEG_y_subida_esperando_respuesta_no_bloquea_el_Centro()
    {
        await SembrarAcreditacionEnAsync(_centroId, _trabajadorId, a => a.MarcarSubida());

        var resultado = await CalcularAsync();

        resultado.Estado.Should().Be(EstadoCentro.Vigente);
        resultado.Causas.Should().NotContain(c => c.Descripcion.Contains(TextoRechazo));
    }

    [Fact]
    public async Task Una_rechazada_en_otro_Centro_no_bloquea_a_este()
    {
        // Este Centro tiene su documento al día y aceptado; el otro, el suyo rechazado.
        await SembrarAcreditacionEnAsync(_centroId, _trabajadorId, a => a.MarcarAceptada(VigenciaEnPlataforma.SinConfirmar));
        var (otroCentro, otroTrabajador) = await SembrarSegundoCentroAsync();
        await SembrarAcreditacionEnAsync(otroCentro, otroTrabajador, Rechazada);

        await using var contexto = CrearContexto();
        var servicio = new CalculoEstadoCentroService(contexto, contexto, contexto, contexto, contexto, contexto);
        var resultados = await servicio.CalcularAsync([_centroId, otroCentro], CancellationToken.None);

        resultados[otroCentro].Estado.Should().Be(EstadoCentro.Bloqueado,
            "control positivo: la rechazada sí bloquea el Centro al que pertenece");
        resultados[_centroId].Estado.Should().Be(EstadoCentro.Vigente);
        resultados[_centroId].Causas.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_rechazada_de_un_trabajador_desvinculado_no_bloquea_el_Centro()
    {
        await SembrarAcreditacionEnAsync(_centroId, _trabajadorId, Rechazada);
        await using (var contexto = CrearContexto())
        {
            var asignacion = await contexto.Asignaciones.SingleAsync(a => a.TrabajadorId == _trabajadorId);
            asignacion.DarDeBaja(DateOnly.FromDateTime(DateTime.UtcNow));
            await contexto.SaveChangesAsync();
        }

        var resultado = await CalcularAsync();

        resultado.Causas.Should().NotContain(c => c.Descripcion.Contains(TextoRechazo));
    }

    [Fact]
    public async Task Una_rechazada_de_un_tipo_que_el_Centro_excluye_no_bloquea()
    {
        await SembrarAcreditacionEnAsync(_centroId, _trabajadorId, Rechazada);
        await using (var contexto = CrearContexto())
        {
            contexto.TiposDocumentoCentros.Add(new TipoDocumentoCentro(_tipoDocumentoObligatorioId, _centroId, incluido: false));
            await contexto.SaveChangesAsync();
        }

        var resultado = await CalcularAsync();

        resultado.Causas.Should().NotContain(c => c.Descripcion.Contains(TextoRechazo),
            "el Centro dejó explícitamente fuera ese tipo: su rechazo en la plataforma no es aplicable aquí");
    }

    [Fact]
    public async Task Una_rechazada_de_un_tipo_no_requerido_por_defecto_solo_bloquea_si_el_Centro_lo_incluye_explicitamente()
    {
        Guid tipoOpcional;
        await using (var contexto = CrearContexto())
        {
            var tipo = new TipoDocumento("Curso opcional", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.No);
            contexto.TiposDocumento.Add(tipo);
            await contexto.SaveChangesAsync();
            tipoOpcional = tipo.Id;
        }

        await SembrarAcreditacionEnAsync(_centroId, _trabajadorId, Rechazada, tipoOpcional);
        // Documento obligatorio al día para que solo la rechazada del opcional esté en juego.
        await SembrarAcreditacionEnAsync(_centroId, _trabajadorId, a => a.MarcarAceptada(VigenciaEnPlataforma.SinConfirmar));

        (await CalcularAsync()).Causas.Should().NotContain(c => c.Descripcion.Contains(TextoRechazo),
            "un tipo opcional sin fila explícita no cuenta para el cumplimiento del Centro");

        await using (var contexto = CrearContexto())
        {
            contexto.TiposDocumentoCentros.Add(new TipoDocumentoCentro(tipoOpcional, _centroId, incluido: true));
            await contexto.SaveChangesAsync();
        }

        var resultado = await CalcularAsync();
        resultado.Estado.Should().Be(EstadoCentro.Bloqueado,
            "control positivo: con fila explícita Incluido, el mismo rechazo sí aplica");
        resultado.Causas.Should().Contain(c => c.Bloqueante && c.Descripcion.Contains(TextoRechazo));
    }

    [Fact]
    public async Task Renovar_el_documento_rechazado_deja_de_bloquear_el_Centro()
    {
        await SembrarAcreditacionEnAsync(_centroId, _trabajadorId, Rechazada);
        (await CalcularAsync()).Estado.Should().Be(EstadoCentro.Bloqueado);

        await using (var contexto = CrearContexto())
        {
            var acreditacion = await contexto.AcreditacionesDocumentoPlataforma.SingleAsync();
            acreditacion.ReiniciarPorRenovacionDocumento();
            await contexto.SaveChangesAsync();
        }

        (await CalcularAsync()).Estado.Should().Be(EstadoCentro.Vigente,
            "renovar reinicia la acreditación a Pendiente de subir: se corrige subiéndolo de nuevo, no a mano");
    }

    /// <summary>
    /// Deja el Centro con su documentación al día en TALVEG y una acreditación
    /// de ese documento contra una plataforma, con la vigencia que se le pase.
    /// Así lo único que cambia entre las tres pruebas de arriba es la vigencia
    /// en la plataforma, que es la variable bajo estudio.
    /// </summary>
    private async Task SembrarAcreditacionAsync(VigenciaEnPlataforma vigencia)
    {
        await using var contexto = CrearContexto();

        var documento = Documento.DeTrabajador(
            _trabajadorId, _tipoDocumentoObligatorioId,
            DateOnly.FromDateTime(DateTime.UtcNow), VigenciaDocumento.VenceEl(DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1)));
        contexto.Documentos.Add(documento);

        var proveedor = new ProveedorPlataformaCae("PLAT-PRUEBA", "Plataforma de prueba");
        contexto.ProveedoresPlataformaCae.Add(proveedor);
        await contexto.SaveChangesAsync();

        var canal = CanalGestionDocumental.DePlataforma(
            _centroId, "Acceso de prueba", proveedor.Id, null, null, null);
        contexto.CanalesGestionDocumental.Add(canal);
        await contexto.SaveChangesAsync();

        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id);
        acreditacion.MarcarAceptada(vigencia);
        contexto.AcreditacionesDocumentoPlataforma.Add(acreditacion);
        await contexto.SaveChangesAsync();
    }

    [Fact]
    public async Task Retorna_Vigente_cuando_el_unico_trabajador_tiene_su_documento_obligatorio_al_dia()
    {
        await using (var contexto = CrearContexto())
        {
            contexto.Documentos.Add(Documento.DeTrabajador(
                _trabajadorId, _tipoDocumentoObligatorioId, DateOnly.FromDateTime(DateTime.UtcNow), VigenciaDocumento.VenceEl(DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1))));
            await contexto.SaveChangesAsync();
        }

        var resultado = await CalcularAsync();

        resultado.Estado.Should().Be(EstadoCentro.Vigente);
        resultado.Causas.Should().BeEmpty();
    }

    [Fact]
    public async Task Retorna_Faltante_cuando_el_trabajador_no_tiene_el_documento_obligatorio()
    {
        var resultado = await CalcularAsync();

        resultado.Estado.Should().Be(EstadoCentro.Faltante);
        resultado.Causas.Should().ContainSingle(c => c.Estado == EstadoDocumento.Faltante && c.Descripcion.Contains("Ana García"));
    }

    [Fact]
    public async Task Retorna_Vencido_cuando_un_documento_de_la_empresa_esta_vencido()
    {
        Guid tipoEmpresaId;
        Guid documentoEmpresaId;
        var fechaVencimiento = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);

        await using (var contexto = CrearContexto())
        {
            var tipoEmpresa = new TipoDocumento("Seguro RC", null, aplicaVencimientoAutomatico: false, 2, AmbitoAplicacion.Empresa);
            contexto.TiposDocumento.Add(tipoEmpresa);
            await contexto.SaveChangesAsync();
            tipoEmpresaId = tipoEmpresa.Id;

            var documentoEmpresa = Documento.DeEmpresa(
                _empresaId, tipoEmpresa.Id, DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-1), VigenciaDocumento.VenceEl(fechaVencimiento));
            contexto.Documentos.Add(documentoEmpresa);
            contexto.Documentos.Add(Documento.DeTrabajador(
                _trabajadorId, _tipoDocumentoObligatorioId, DateOnly.FromDateTime(DateTime.UtcNow), VigenciaDocumento.VenceEl(DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1))));
            await contexto.SaveChangesAsync();
            documentoEmpresaId = documentoEmpresa.Id;
        }

        var resultado = await CalcularAsync();

        resultado.Estado.Should().Be(EstadoCentro.Vencido);
        // DocumentoId/TipoDocumentoId/FechaVencimiento alimentan la tabla de
        // documentos del bloque Empresa en Centro 360 (Documento · Estado ·
        // Vigencia · Acción) sin una consulta aparte — deben venir poblados.
        resultado.Causas.Should().ContainSingle(c =>
            c.Estado == EstadoDocumento.Vencido && c.Descripcion.Contains("Empresa")
            && c.DocumentoId == documentoEmpresaId && c.TipoDocumentoId == tipoEmpresaId && c.FechaVencimiento == fechaVencimiento);
    }

    [Fact]
    public async Task Retorna_Bloqueado_cuando_falta_un_documento_bloqueante_aunque_todo_lo_demas_este_vigente()
    {
        Guid tipoBloqueanteId;
        await using (var contexto = CrearContexto())
        {
            contexto.Documentos.Add(Documento.DeTrabajador(
                _trabajadorId, _tipoDocumentoObligatorioId, DateOnly.FromDateTime(DateTime.UtcNow), VigenciaDocumento.VenceEl(DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1))));

            var tipoBloqueante = new TipoDocumento("Formulario de acceso", null, false, 3, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.No);
            contexto.TiposDocumento.Add(tipoBloqueante);
            await contexto.SaveChangesAsync();
            tipoBloqueanteId = tipoBloqueante.Id;

            contexto.TiposDocumentoCentros.Add(new TipoDocumentoCentro(tipoBloqueanteId, _centroId, incluido: true, bloqueaAcceso: true));
            await contexto.SaveChangesAsync();
        }

        var resultado = await CalcularAsync();

        resultado.Estado.Should().Be(EstadoCentro.Bloqueado);
        resultado.Causas.Should().ContainSingle(c => c.Bloqueante && c.Descripcion.Contains("Formulario de acceso") && c.Descripcion.Contains("Ana García"));
    }

    [Fact]
    public async Task Subir_el_documento_bloqueante_deja_de_bloquear_el_centro()
    {
        await using (var contexto = CrearContexto())
        {
            contexto.Documentos.Add(Documento.DeTrabajador(
                _trabajadorId, _tipoDocumentoObligatorioId, DateOnly.FromDateTime(DateTime.UtcNow), VigenciaDocumento.VenceEl(DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1))));

            var tipoBloqueante = new TipoDocumento("Formulario de acceso", null, false, 3, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.No);
            contexto.TiposDocumento.Add(tipoBloqueante);
            await contexto.SaveChangesAsync();

            contexto.TiposDocumentoCentros.Add(new TipoDocumentoCentro(tipoBloqueante.Id, _centroId, incluido: true, bloqueaAcceso: true));
            contexto.Documentos.Add(Documento.DeTrabajador(
                _trabajadorId, tipoBloqueante.Id, DateOnly.FromDateTime(DateTime.UtcNow), VigenciaDocumento.VenceEl(DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1))));
            await contexto.SaveChangesAsync();
        }

        var resultado = await CalcularAsync();

        resultado.Estado.Should().Be(EstadoCentro.Vigente);
        resultado.Causas.Should().BeEmpty();
    }

    /// <summary>
    /// Premisa del estado vacío de "Centros con menor cumplimiento"
    /// (<c>ObtenerCatalogoKpisQuery</c> solo lista centros con
    /// <c>Requeridos &gt; 0</c>): un centro que SÍ pide documentación de
    /// trabajador —aquí por el valor general del tipo, sin fila propia, igual
    /// que el centro del fixture— pero sin ningún trabajador asignado cuenta
    /// cero requeridos, exactamente como uno que no pide nada. Por eso el
    /// dashboard no puede atribuir la lista vacía a que ningún centro la pida.
    /// Se ejercitan las dos salidas: el retorno temprano cuando ninguno de los
    /// centros tiene asignaciones y el bucle cuando solo algunos las tienen.
    /// </summary>
    [Fact]
    public async Task Un_centro_que_pide_documentacion_de_trabajador_pero_sin_trabajadores_asignados_cuenta_cero_requeridos()
    {
        Guid centroSinTrabajadoresId;
        await using (var contexto = CrearContexto())
        {
            var centroConTrabajadores = await contexto.Centros.SingleAsync(c => c.Id == _centroId);
            var centroSinTrabajadores = new Centro(centroConTrabajadores.ClienteId, _empresaId, "Centro sin trabajadores");
            contexto.Centros.Add(centroSinTrabajadores);
            await contexto.SaveChangesAsync();
            centroSinTrabajadoresId = centroSinTrabajadores.Id;
        }

        await using var lectura = CrearContexto();
        var servicio = new CalculoEstadoCentroService(lectura, lectura, lectura, lectura, lectura, lectura);

        var ambos = await servicio.CalcularCumplimientoAsync([_centroId, centroSinTrabajadoresId], CancellationToken.None);
        var soloElVacio = await servicio.CalcularCumplimientoAsync([centroSinTrabajadoresId], CancellationToken.None);

        ambos[_centroId].Requeridos.Should().Be(1,
            "control positivo: el mismo tipo, sin fila de centro, se pide y se evalúa en cuanto hay un trabajador asignado");
        ambos[centroSinTrabajadoresId].Requeridos.Should().Be(0,
            "sin trabajadores asignados no hay pares trabajador × tipo que evaluar, aunque el centro pida el tipo");
        soloElVacio[centroSinTrabajadoresId].Requeridos.Should().Be(0,
            "misma respuesta por el retorno temprano, cuando ningún centro pedido tiene asignaciones");
    }

    /// <summary>Acreditación del documento vigente del Trabajador contra un canal de plataforma del Centro indicado.</summary>
    private async Task SembrarAcreditacionEnAsync(
        Guid centroId, Guid trabajadorId, Action<AcreditacionDocumentoPlataforma> configurar, Guid? tipoDocumentoId = null)
    {
        await using var contexto = CrearContexto();

        var documento = Documento.DeTrabajador(
            trabajadorId, tipoDocumentoId ?? _tipoDocumentoObligatorioId,
            DateOnly.FromDateTime(DateTime.UtcNow), VigenciaDocumento.VenceEl(DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1)));
        contexto.Documentos.Add(documento);

        var proveedor = new ProveedorPlataformaCae($"PLAT-{Guid.NewGuid():N}"[..14], "Plataforma de prueba");
        contexto.ProveedoresPlataformaCae.Add(proveedor);
        await contexto.SaveChangesAsync();

        var canal = CanalGestionDocumental.DePlataforma(centroId, "Acceso de prueba", proveedor.Id, null, null, null);
        contexto.CanalesGestionDocumental.Add(canal);
        await contexto.SaveChangesAsync();

        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id);
        configurar(acreditacion);
        contexto.AcreditacionesDocumentoPlataforma.Add(acreditacion);
        await contexto.SaveChangesAsync();
    }

    /// <summary>Otro Centro del mismo Tenant, con su propio Trabajador asignado.</summary>
    private async Task<(Guid CentroId, Guid TrabajadorId)> SembrarSegundoCentroAsync()
    {
        await using var contexto = CrearContexto();
        var cliente = Empresa.CrearComoCliente("Otro Cliente EstadoCentro S.L.", "B10380186", false, null, null);
        contexto.Empresas.Add(cliente);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, _empresaId, "Otro Centro EstadoCentro");
        var trabajador = Trabajador.DeEmpresa(_empresaId, "Luis", "Prado", "22334455Y");
        contexto.Centros.Add(centro);
        contexto.Trabajadores.Add(trabajador);
        await contexto.SaveChangesAsync();

        contexto.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, DateOnly.FromDateTime(DateTime.UtcNow)));
        await contexto.SaveChangesAsync();
        return (centro.Id, trabajador.Id);
    }

    private async Task<ResultadoEstadoCentro> CalcularAsync()
    {
        await using var contexto = CrearContexto();
        var servicio = new CalculoEstadoCentroService(contexto, contexto, contexto, contexto, contexto, contexto);

        var resultados = await servicio.CalcularAsync([_centroId], CancellationToken.None);
        return resultados[_centroId];
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
