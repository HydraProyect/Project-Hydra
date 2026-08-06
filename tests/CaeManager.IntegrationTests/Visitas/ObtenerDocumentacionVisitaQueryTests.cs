using CaeManager.Application.Visitas.Queries.ObtenerDocumentacionVisita;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Visitas;

/// <summary>
/// A diferencia de PestanaDocumentacion (todos los Documentos de una
/// Empresa/Trabajador sin importar el Centro), esta Query solo muestra lo
/// que aplica al Centro de la visita, incluye "Faltante" para Empresa y
/// Trabajador, y ordena por severidad.
/// </summary>
public class ObtenerDocumentacionVisitaQueryTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _visitaId;
    private Guid _centroId;
    private Guid _empresaId;
    private Guid _trabajadorId;
    private Guid _tipoObligatorioEmpresaId;
    private Guid _tipoObligatorioTrabajadorId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        if (await contexto.ParametrosSistema.SingleOrDefaultAsync() is null)
            contexto.ParametrosSistema.Add(new ParametroSistema(30, 15));

        var cliente = new Cliente("Cliente Doc Visita S.L.", "B12345674", esCritico: false);
        var empresa = new Empresa("Empresa Doc Visita S.L.", "B87654323");
        contexto.Clientes.Add(cliente);
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro Doc Visita");
        contexto.Centros.Add(centro);

        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Ana", "García", "77189989B");
        contexto.Trabajadores.Add(trabajador);

        var tipoEmpresa = new TipoDocumento("Seguro RC", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Empresa, esObligatorio: true);
        var tipoTrabajador = new TipoDocumento("EPIs", null, aplicaVencimientoAutomatico: false, 2, AmbitoAplicacion.Trabajador, esObligatorio: true);
        contexto.TiposDocumento.AddRange(tipoEmpresa, tipoTrabajador);
        await contexto.SaveChangesAsync();

        var visita = new Visita(centro.Id, DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5), null);
        contexto.Visitas.Add(visita);
        await contexto.SaveChangesAsync();

        contexto.VisitasTrabajadores.Add(new VisitaTrabajador(visita.Id, trabajador.Id));
        await contexto.SaveChangesAsync();

        _visitaId = visita.Id;
        _centroId = centro.Id;
        _empresaId = empresa.Id;
        _trabajadorId = trabajador.Id;
        _tipoObligatorioEmpresaId = tipoEmpresa.Id;
        _tipoObligatorioTrabajadorId = tipoTrabajador.Id;
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Un_tipo_obligatorio_de_empresa_sin_documento_aparece_como_faltante()
    {
        var resultado = await EjecutarAsync();

        resultado!.Empresa.PeorEstado.Should().Be(EstadoDocumento.Faltante);
        resultado.Empresa.Documentos.Should().ContainSingle(d => d.Estado == EstadoDocumento.Faltante && d.TipoDocumentoId == _tipoObligatorioEmpresaId);
    }

    [Fact]
    public async Task Un_tipo_obligatorio_de_trabajador_sin_documento_aparece_como_faltante()
    {
        var resultado = await EjecutarAsync();

        var trabajador = resultado!.Trabajadores.Should().ContainSingle().Subject;
        trabajador.TrabajadorId.Should().Be(_trabajadorId);
        trabajador.Documentacion.PeorEstado.Should().Be(EstadoDocumento.Faltante);
        trabajador.Documentacion.Documentos.Should().ContainSingle(d => d.Estado == EstadoDocumento.Faltante && d.TipoDocumentoId == _tipoObligatorioTrabajadorId);
    }

    [Fact]
    public async Task Un_documento_vencido_del_trabajador_excluido_explicitamente_de_este_centro_no_aparece()
    {
        await using (var contexto = CrearContexto())
        {
            // Exclusión explícita del tipo obligatorio de trabajador en ESTE
            // centro (Incluido=false, PLAN-EJECUCION-UX.md § 0.4) — sustituye a
            // la semántica antigua de "restringir a otro centro" (allow-list
            // global): ahora la posición se declara por par (Tipo, Centro), no
            // por presencia de filas en otros centros.
            contexto.TiposDocumentoCentros.Add(new TipoDocumentoCentro(_tipoObligatorioTrabajadorId, _centroId, incluido: false));

            contexto.Documentos.Add(Documento.DeTrabajador(
                _trabajadorId, _tipoObligatorioTrabajadorId, DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-1), DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1)));
            await contexto.SaveChangesAsync();
        }

        var resultado = await EjecutarAsync();

        var trabajador = resultado!.Trabajadores.Should().ContainSingle().Subject;
        trabajador.Documentacion.Documentos.Should().BeEmpty();
        trabajador.Documentacion.PeorEstado.Should().Be(EstadoDocumento.Vigente);
    }

    [Fact]
    public async Task Ordena_por_severidad_faltante_primero_y_vigente_al_final()
    {
        await using (var contexto = CrearContexto())
        {
            var tipoVigente = new TipoDocumento("Formación", null, aplicaVencimientoAutomatico: false, 3, AmbitoAplicacion.Trabajador);
            contexto.TiposDocumento.Add(tipoVigente);
            await contexto.SaveChangesAsync();

            contexto.Documentos.Add(Documento.DeTrabajador(
                _trabajadorId, tipoVigente.Id, DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1)));
            await contexto.SaveChangesAsync();
        }

        var resultado = await EjecutarAsync();

        var trabajador = resultado!.Trabajadores.Should().ContainSingle().Subject;
        trabajador.Documentacion.Documentos.Should().HaveCount(2);
        trabajador.Documentacion.Documentos[0].Estado.Should().Be(EstadoDocumento.Faltante);
        trabajador.Documentacion.Documentos[^1].Estado.Should().Be(EstadoDocumento.Vigente);
    }

    [Fact]
    public async Task Resuelve_el_empleador_como_subcontrata_cuando_el_trabajador_es_de_subcontrata()
    {
        Guid trabajadorSubId;
        await using (var contexto = CrearContexto())
        {
            var subcontrata = new Subcontrata("Subcontrata Doc Visita S.L.");
            contexto.Subcontratas.Add(subcontrata);
            await contexto.SaveChangesAsync();

            var trabajadorSub = Trabajador.DeSubcontrata(subcontrata.Id, "Luis", "Pérez", "12345678Z");
            contexto.Trabajadores.Add(trabajadorSub);
            await contexto.SaveChangesAsync();
            trabajadorSubId = trabajadorSub.Id;

            contexto.VisitasTrabajadores.Add(new VisitaTrabajador(_visitaId, trabajadorSubId));
            await contexto.SaveChangesAsync();
        }

        var resultado = await EjecutarAsync();

        var trabajadorEncontrado = resultado!.Trabajadores.Should().Contain(t => t.TrabajadorId == trabajadorSubId).Subject;
        trabajadorEncontrado.EmpleadorNombre.Should().Be("Subcontrata Doc Visita S.L.");
    }

    private async Task<DocumentacionVisitaDto?> EjecutarAsync()
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerDocumentacionVisitaQueryHandler(
            contexto, contexto, contexto, contexto, contexto, contexto, contexto, contexto, new AlcanceDatosServiceFalso());

        return await handler.Handle(new ObtenerDocumentacionVisitaQuery(_visitaId), CancellationToken.None);
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
