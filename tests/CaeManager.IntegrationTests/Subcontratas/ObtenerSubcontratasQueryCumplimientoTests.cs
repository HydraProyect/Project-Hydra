using CaeManager.Application.Subcontratas;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratas;
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
/// Primer nivel de "Subcontrata 360": % de cumplimiento y recuentos de la
/// fila de <c>/subcontratas</c>, calculados por <see cref="CalculoEstadoSubcontrataService"/>
/// — mismo alcance que el badge de Centro 360, sin ambito Empresa (Documento
/// no tiene propietario Subcontrata).
/// </summary>
public class ObtenerSubcontratasQueryCumplimientoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _subcontrataId;
    private Guid _centroId;
    private Guid _tipoObligatorioId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        if (await contexto.ParametrosSistema.SingleOrDefaultAsync() is null)
            contexto.ParametrosSistema.Add(new ParametroSistema(30, 15));

        var cliente = Empresa.CrearComoCliente("Cliente Cumplimiento Sub S.L.", "B12345674", false, null, null);
        var empresa = new Empresa("Empresa Cumplimiento Sub S.L.", "B87654323");
        contexto.Empresas.Add(cliente);
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro Cumplimiento Sub");
        contexto.Centros.Add(centro);

        var subcontrata = Empresa.CrearComoSubcontrata("Subcontrata Cumplimiento S.L.", null, NivelServicioSubcontrata.Gestionada.ToString());
        contexto.Empresas.Add(subcontrata);

        var tipoObligatorio = new TipoDocumento("EPIs", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
        contexto.TiposDocumento.Add(tipoObligatorio);
        await contexto.SaveChangesAsync();

        _subcontrataId = subcontrata.Id;
        _centroId = centro.Id;
        _tipoObligatorioId = tipoObligatorio.Id;
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Sin_trabajadores_con_requisitos_el_cumplimiento_es_nulo()
    {
        var resultado = await EjecutarAsync();

        var fila = resultado.Elementos.Should().ContainSingle().Subject;
        fila.CumplimientoPorcentaje.Should().BeNull();
        fila.Recuentos.TotalVencidas.Should().Be(0);
        fila.Recuentos.TotalProximas.Should().Be(0);
    }

    [Fact]
    public async Task Un_documento_obligatorio_faltante_cuenta_como_incumplimiento_al_0_por_ciento()
    {
        await using (var contexto = CrearContexto())
        {
            var trabajador = Trabajador.DeSubcontrata(_subcontrataId, "Sara", "Faltante", "12345678Z");
            contexto.Trabajadores.Add(trabajador);
            await contexto.SaveChangesAsync();

            contexto.Asignaciones.Add(new Asignacion(trabajador.Id, _centroId, DateOnly.FromDateTime(DateTime.UtcNow)));
            await contexto.SaveChangesAsync();
        }

        var resultado = await EjecutarAsync();

        var fila = resultado.Elementos.Should().ContainSingle().Subject;
        fila.CumplimientoPorcentaje.Should().Be(0);
        fila.Recuentos.TotalVencidas.Should().Be(1);
    }

    [Fact]
    public async Task Un_documento_vigente_cuenta_como_cumplimiento_al_100_por_ciento()
    {
        await using (var contexto = CrearContexto())
        {
            var trabajador = Trabajador.DeSubcontrata(_subcontrataId, "Julio", "Aldia", "77189989B");
            contexto.Trabajadores.Add(trabajador);
            await contexto.SaveChangesAsync();

            contexto.Asignaciones.Add(new Asignacion(trabajador.Id, _centroId, DateOnly.FromDateTime(DateTime.UtcNow)));
            contexto.Documentos.Add(Documento.DeTrabajador(
                trabajador.Id, _tipoObligatorioId, DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1)));
            await contexto.SaveChangesAsync();
        }

        var resultado = await EjecutarAsync();

        var fila = resultado.Elementos.Should().ContainSingle().Subject;
        fila.CumplimientoPorcentaje.Should().Be(100);
        fila.Recuentos.TotalVencidas.Should().Be(0);
    }

    [Fact]
    public async Task El_nivel_de_servicio_viaja_en_la_fila_de_la_lista()
    {
        await using (var contexto = CrearContexto())
        {
            var subcontrata = await contexto.Empresas.SingleAsync(e => e.Id == _subcontrataId);
            subcontrata.CambiarNivelServicioComoSubcontrata(NivelServicioSubcontrata.Supervisada.ToString());
            await contexto.SaveChangesAsync();
        }

        var resultado = await EjecutarAsync();

        var fila = resultado.Elementos.Should().ContainSingle().Subject;
        fila.NivelServicio.Should().Be(NivelServicioSubcontrata.Supervisada);
    }

    [Fact]
    public async Task Un_documento_faltante_de_un_trabajador_fuera_de_cartera_no_cuenta_en_el_cumplimiento()
    {
        Guid trabajadorFueraDeCarteraId;
        await using (var contexto = CrearContexto())
        {
            var trabajador = Trabajador.DeSubcontrata(_subcontrataId, "Nora", "Nocartera", "55667788Z");
            contexto.Trabajadores.Add(trabajador);
            await contexto.SaveChangesAsync();
            trabajadorFueraDeCarteraId = trabajador.Id;

            contexto.Asignaciones.Add(new Asignacion(trabajadorFueraDeCarteraId, _centroId, DateOnly.FromDateTime(DateTime.UtcNow)));
            await contexto.SaveChangesAsync();
        }

        // Cartera vacía a propósito (ningún trabajador visible): mismo
        // criterio que ObtenerTrabajadoresQuery — un requisito faltante de un
        // trabajador fuera de cartera no debe aparecer en el anillo de
        // cumplimiento ni en los recuentos de la fila.
        var resultado = await EjecutarAsync(new AlcanceDatosServiceFalso(trabajadorIds: []));

        var fila = resultado.Elementos.Should().ContainSingle().Subject;
        fila.CumplimientoPorcentaje.Should().BeNull();
        fila.Recuentos.TotalVencidas.Should().Be(0);
    }

    private Task<CaeManager.Application.Common.ResultadoPaginado<SubcontrataListaDto>> EjecutarAsync() =>
        EjecutarAsync(new AlcanceDatosServiceFalso());

    private async Task<CaeManager.Application.Common.ResultadoPaginado<SubcontrataListaDto>> EjecutarAsync(AlcanceDatosServiceFalso alcance)
    {
        await using var contexto = CrearContexto();
        var servicio = new CalculoEstadoSubcontrataService(contexto, contexto, contexto, contexto, contexto, alcance);
        var handler = new ObtenerSubcontratasQueryHandler(contexto, alcance, servicio);

        return await handler.Handle(new ObtenerSubcontratasQuery(Busqueda: null), CancellationToken.None);
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
