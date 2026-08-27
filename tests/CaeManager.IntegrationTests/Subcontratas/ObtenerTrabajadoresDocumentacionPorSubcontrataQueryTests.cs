using CaeManager.Application.Subcontratas.Queries.ObtenerTrabajadoresDocumentacionPorSubcontrata;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Subcontratas;

/// <summary>
/// Segundo/tercer nivel de "Subcontrata 360" — mismo alcance que
/// ObtenerAsignacionesDocumentacionPorCentroQueryTests (Centro 360), pero
/// clave por Trabajador (no por Asignación): un Trabajador de Subcontrata
/// puede estar activo en más de un Centro, y aquí interesa la unión de lo
/// que le exige cada uno, no una fila por Asignación.
/// </summary>
public class ObtenerTrabajadoresDocumentacionPorSubcontrataQueryTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _subcontrataId;
    private Guid _centroAId;
    private Guid _centroBId;
    private Guid _tipoAId;
    private Guid _tipoBId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        if (await contexto.ParametrosSistema.SingleOrDefaultAsync() is null)
            contexto.ParametrosSistema.Add(new ParametroSistema(30, 15));

        var cliente = Empresa.CrearComoCliente("Cliente Subcontrata 360 S.L.", "B12345674", false, null, null);
        var empresa = new Empresa("Empresa Subcontrata 360 S.L.", "B87654323");
        contexto.Empresas.Add(cliente);
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var centroA = new Centro(cliente.Id, empresa.Id, "Centro A");
        var centroB = new Centro(cliente.Id, empresa.Id, "Centro B");
        contexto.Centros.AddRange(centroA, centroB);

        var subcontrata = Empresa.CrearComoSubcontrata("Subcontrata 360 Demo S.L.", null, NivelServicioSubcontrata.Gestionada.ToString());
        contexto.Empresas.Add(subcontrata);

        var tipoA = new TipoDocumento("Tipo A", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.No);
        var tipoB = new TipoDocumento("Tipo B", null, aplicaVencimientoAutomatico: false, 2, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.No);
        contexto.TiposDocumento.AddRange(tipoA, tipoB);
        await contexto.SaveChangesAsync();

        // Cada centro exige un tipo distinto — ninguno obligatorio
        // globalmente, solo por TipoDocumentoCentro explícito.
        contexto.TiposDocumentoCentros.Add(new TipoDocumentoCentro(tipoA.Id, centroA.Id, incluido: true));
        contexto.TiposDocumentoCentros.Add(new TipoDocumentoCentro(tipoB.Id, centroB.Id, incluido: true));
        await contexto.SaveChangesAsync();

        _subcontrataId = subcontrata.Id;
        _centroAId = centroA.Id;
        _centroBId = centroB.Id;
        _tipoAId = tipoA.Id;
        _tipoBId = tipoB.Id;
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Un_trabajador_sin_asignaciones_activas_no_tiene_documentos_exigidos()
    {
        await using (var contexto = CrearContexto())
        {
            contexto.Trabajadores.Add(Trabajador.DeSubcontrata(_subcontrataId, "Ana", "Sinasignar", "12345678Z"));
            await contexto.SaveChangesAsync();
        }

        var resultado = await EjecutarAsync();

        var trabajador = resultado.Should().ContainSingle().Subject;
        trabajador.Documentos.Should().BeEmpty();
        trabajador.PeorEstado.Should().Be(EstadoDocumento.Vigente);
    }

    [Fact]
    public async Task Un_trabajador_con_asignacion_a_un_centro_ve_faltante_lo_que_ese_centro_exige()
    {
        Guid trabajadorId;
        await using (var contexto = CrearContexto())
        {
            var trabajador = Trabajador.DeSubcontrata(_subcontrataId, "Luis", "Conasignacion", "77189989B");
            contexto.Trabajadores.Add(trabajador);
            await contexto.SaveChangesAsync();
            trabajadorId = trabajador.Id;

            contexto.Asignaciones.Add(new Asignacion(trabajadorId, _centroAId, DateOnly.FromDateTime(DateTime.UtcNow)));
            await contexto.SaveChangesAsync();
        }

        var resultado = await EjecutarAsync();

        var fila = resultado.Should().ContainSingle().Subject;
        fila.TrabajadorId.Should().Be(trabajadorId);
        fila.PeorEstado.Should().Be(EstadoDocumento.Faltante);
        fila.Documentos.Should().ContainSingle(d => d.TipoDocumentoId == _tipoAId && d.Estado == EstadoDocumento.Faltante);
    }

    [Fact]
    public async Task Un_trabajador_activo_en_dos_centros_ve_la_union_de_lo_que_exige_cada_uno()
    {
        Guid trabajadorId;
        await using (var contexto = CrearContexto())
        {
            var trabajador = Trabajador.DeSubcontrata(_subcontrataId, "Marta", "Dositios", "22334455Y");
            contexto.Trabajadores.Add(trabajador);
            await contexto.SaveChangesAsync();
            trabajadorId = trabajador.Id;

            contexto.Asignaciones.Add(new Asignacion(trabajadorId, _centroAId, DateOnly.FromDateTime(DateTime.UtcNow)));
            contexto.Asignaciones.Add(new Asignacion(trabajadorId, _centroBId, DateOnly.FromDateTime(DateTime.UtcNow)));
            await contexto.SaveChangesAsync();
        }

        var resultado = await EjecutarAsync();

        var fila = resultado.Should().ContainSingle().Subject;
        fila.Documentos.Should().HaveCount(2);
        fila.Documentos.Should().Contain(d => d.TipoDocumentoId == _tipoAId);
        fila.Documentos.Should().Contain(d => d.TipoDocumentoId == _tipoBId);
    }

    [Fact]
    public async Task Un_documento_vigente_ya_subido_no_aparece_como_faltante()
    {
        Guid trabajadorId;
        await using (var contexto = CrearContexto())
        {
            var trabajador = Trabajador.DeSubcontrata(_subcontrataId, "Pedro", "Aldiadocumental", "33445566R");
            contexto.Trabajadores.Add(trabajador);
            await contexto.SaveChangesAsync();
            trabajadorId = trabajador.Id;

            contexto.Asignaciones.Add(new Asignacion(trabajadorId, _centroAId, DateOnly.FromDateTime(DateTime.UtcNow)));
            contexto.Documentos.Add(Documento.DeTrabajador(
                trabajadorId, _tipoAId, DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1)));
            await contexto.SaveChangesAsync();
        }

        var resultado = await EjecutarAsync();

        var fila = resultado.Should().ContainSingle().Subject;
        fila.PeorEstado.Should().Be(EstadoDocumento.Vigente);
        fila.Documentos.Should().ContainSingle(d => d.TipoDocumentoId == _tipoAId && d.Estado == EstadoDocumento.Vigente && d.DocumentoId != null);
    }

    [Fact]
    public async Task Un_trabajador_fuera_de_la_cartera_visible_no_aparece()
    {
        Guid trabajadorVisibleId, trabajadorFueraDeCarteraId;
        await using (var contexto = CrearContexto())
        {
            var visible = Trabajador.DeSubcontrata(_subcontrataId, "Elena", "Visible", "44556677L");
            var fueraDeCartera = Trabajador.DeSubcontrata(_subcontrataId, "Nora", "Nocartera", "55667788Z");
            contexto.Trabajadores.AddRange(visible, fueraDeCartera);
            await contexto.SaveChangesAsync();
            trabajadorVisibleId = visible.Id;
            trabajadorFueraDeCarteraId = fueraDeCartera.Id;
        }

        // Mismo criterio que ObtenerTrabajadoresQuery: la Subcontrata visible
        // no implica que todos sus Trabajadores lo sean para la cartera del
        // usuario actual (regresión: la primera versión de esta Query no
        // aplicaba este filtro y exponía trabajadores fuera de cartera que
        // ObtenerTrabajadorPorIdQuery luego rechazaba al abrir el panel).
        var resultado = await EjecutarAsync(new AlcanceDatosServiceFalso(trabajadorIds: [trabajadorVisibleId]));

        resultado.Should().ContainSingle(t => t.TrabajadorId == trabajadorVisibleId);
        resultado.Should().NotContain(t => t.TrabajadorId == trabajadorFueraDeCarteraId);
    }

    private Task<IReadOnlyList<TrabajadorDocumentacionSubcontrataDto>> EjecutarAsync() =>
        EjecutarAsync(new AlcanceDatosServiceFalso());

    private async Task<IReadOnlyList<TrabajadorDocumentacionSubcontrataDto>> EjecutarAsync(AlcanceDatosServiceFalso alcance)
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerTrabajadoresDocumentacionPorSubcontrataQueryHandler(
            contexto, contexto, contexto, contexto, contexto, alcance);

        return await handler.Handle(new ObtenerTrabajadoresDocumentacionPorSubcontrataQuery(_subcontrataId), CancellationToken.None);
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
