using CaeManager.Application.Trabajadores.Queries.ObtenerDocumentacionPorCentroDeTrabajador;
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

namespace CaeManager.IntegrationTests.Trabajadores;

/// <summary>
/// Pestaña Operación de Trabajador 360. El índice (TrabajadorId, TipoDocumentoId)
/// de Documento no es único y CrearDocumento no rechaza un segundo documento del
/// mismo tipo (la renovación típica: el vencido sigue ahí y se sube el nuevo), así
/// que la Query debe elegir UN documento por tipo en vez de fallar.
/// </summary>
public class ObtenerDocumentacionPorCentroDeTrabajadorQueryTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _trabajadorId;
    private Guid _tipoId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        if (await contexto.ParametrosSistema.SingleOrDefaultAsync() is null)
            contexto.ParametrosSistema.Add(new ParametroSistema(30, 15));

        var cliente = Empresa.CrearComoCliente("Cliente Trabajador 360 S.L.", "B12345674", false, null, null);
        var empresa = new Empresa("Empresa Trabajador 360 S.L.", "B87654323");
        contexto.Empresas.AddRange(cliente, empresa);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro Trabajador 360");
        contexto.Centros.Add(centro);

        var subcontrata = Empresa.CrearComoSubcontrata("Subcontrata Trabajador 360 S.L.", null, NivelServicioSubcontrata.Gestionada.ToString());
        contexto.Empresas.Add(subcontrata);

        var tipo = new TipoDocumento("Formación PRL", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
        contexto.TiposDocumento.Add(tipo);
        await contexto.SaveChangesAsync();

        var trabajador = Trabajador.DeSubcontrata(subcontrata.Id, "Rosa", "Renovada", "12345678Z");
        contexto.Trabajadores.Add(trabajador);
        await contexto.SaveChangesAsync();

        contexto.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, DateOnly.FromDateTime(DateTime.UtcNow)));
        await contexto.SaveChangesAsync();

        _trabajadorId = trabajador.Id;
        _tipoId = tipo.Id;
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Con_el_vencido_y_su_renovacion_del_mismo_tipo_representa_al_tipo_el_vigente()
    {
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        Guid renovacionId;
        await using (var contexto = CrearContexto())
        {
            // La renovación se inserta primero y el vencido después, para que
            // «el último leído» no coincida por casualidad con el correcto.
            var renovacion = Documento.DeTrabajador(_trabajadorId, _tipoId, hoy.AddDays(-1), VigenciaDocumento.VenceEl(hoy.AddYears(1)));
            var vencido = Documento.DeTrabajador(_trabajadorId, _tipoId, hoy.AddYears(-2), VigenciaDocumento.VenceEl(hoy.AddDays(-10)));
            contexto.Documentos.Add(renovacion);
            await contexto.SaveChangesAsync();
            contexto.Documentos.Add(vencido);
            await contexto.SaveChangesAsync();
            renovacionId = renovacion.Id;
        }

        var resultado = await EjecutarAsync();

        var centro = resultado.Should().ContainSingle().Subject;
        var documento = centro.Documentos.Should().ContainSingle(d => d.TipoDocumentoId == _tipoId).Subject;
        documento.DocumentoId.Should().Be(renovacionId);
        documento.Estado.Should().Be(EstadoDocumento.Vigente);
        centro.PeorEstado.Should().Be(EstadoDocumento.Vigente);
    }

    [Fact]
    public async Task Con_dos_vencidos_del_mismo_tipo_representa_al_tipo_el_mas_reciente_y_se_ve_vencido()
    {
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        Guid masRecienteId;
        await using (var contexto = CrearContexto())
        {
            var masReciente = Documento.DeTrabajador(_trabajadorId, _tipoId, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(-5)));
            var antiguo = Documento.DeTrabajador(_trabajadorId, _tipoId, hoy.AddYears(-3), VigenciaDocumento.VenceEl(hoy.AddYears(-2)));
            contexto.Documentos.Add(masReciente);
            await contexto.SaveChangesAsync();
            contexto.Documentos.Add(antiguo);
            await contexto.SaveChangesAsync();
            masRecienteId = masReciente.Id;
        }

        var resultado = await EjecutarAsync();

        var centro = resultado.Should().ContainSingle().Subject;
        var documento = centro.Documentos.Should().ContainSingle(d => d.TipoDocumentoId == _tipoId).Subject;
        documento.DocumentoId.Should().Be(masRecienteId);
        documento.Estado.Should().Be(EstadoDocumento.Vencido);
        documento.FechaVencimiento.Should().Be(hoy.AddDays(-5));
    }

    private async Task<IReadOnlyList<CentroDocumentacionTrabajadorDto>> EjecutarAsync()
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerDocumentacionPorCentroDeTrabajadorQueryHandler(
            contexto, contexto, contexto, contexto, contexto, contexto, new AlcanceDatosServiceFalso());

        return await handler.Handle(new ObtenerDocumentacionPorCentroDeTrabajadorQuery(_trabajadorId), CancellationToken.None);
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
