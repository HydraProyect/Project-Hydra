using CaeManager.Application.Centros;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
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

    [Fact]
    public async Task Retorna_Vigente_cuando_el_unico_trabajador_tiene_su_documento_obligatorio_al_dia()
    {
        await using (var contexto = CrearContexto())
        {
            contexto.Documentos.Add(Documento.DeTrabajador(
                _trabajadorId, _tipoDocumentoObligatorioId, DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1)));
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
                _empresaId, tipoEmpresa.Id, DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-1), fechaVencimiento);
            contexto.Documentos.Add(documentoEmpresa);
            contexto.Documentos.Add(Documento.DeTrabajador(
                _trabajadorId, _tipoDocumentoObligatorioId, DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1)));
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
                _trabajadorId, _tipoDocumentoObligatorioId, DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1)));

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
                _trabajadorId, _tipoDocumentoObligatorioId, DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1)));

            var tipoBloqueante = new TipoDocumento("Formulario de acceso", null, false, 3, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.No);
            contexto.TiposDocumento.Add(tipoBloqueante);
            await contexto.SaveChangesAsync();

            contexto.TiposDocumentoCentros.Add(new TipoDocumentoCentro(tipoBloqueante.Id, _centroId, incluido: true, bloqueaAcceso: true));
            contexto.Documentos.Add(Documento.DeTrabajador(
                _trabajadorId, tipoBloqueante.Id, DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1)));
            await contexto.SaveChangesAsync();
        }

        var resultado = await CalcularAsync();

        resultado.Estado.Should().Be(EstadoCentro.Vigente);
        resultado.Causas.Should().BeEmpty();
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
