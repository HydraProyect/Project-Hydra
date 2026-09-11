using CaeManager.Application.Subcontratas.Commands.RegistrarVerificacionExterna;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using CaeManager.Domain.Subcontratas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Subcontratas;

/// <summary>
/// Revisión de Codex 2026-09-11: contra PostgreSQL real (con RLS de tenant),
/// no solo con los fakes de Application — el defecto era justo que la
/// comprobación de alcance se salía del filtro de tenant sin cruzar con la
/// cartera real de la Subcontrata. Ver
/// RegistrarVerificacionExternaSubcontrataCommandHandlerTests (Application)
/// para los mismos escenarios sin base de datos.
/// </summary>
public class RegistrarVerificacionExternaSubcontrataCommandTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _subcontrataId;
    private Guid _centroPropioId;
    private Guid _centroAjenoId;
    private Guid _tipoTrabajadorId;
    private Guid _tipoClienteId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var clientePropio = Empresa.CrearComoCliente("Refrielectric S.A.", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
        var clienteAjeno = Empresa.CrearComoCliente("Sin relación con esta subcontrata S.L.", "B10380186", esCritico: false, notas: null, ejecutivoUsuarioId: null);
        var empresaPropia = new Empresa("TALVEG Demo S.L.", "B10380194");
        var subcontrata = Empresa.CrearComoSubcontrata("Andamios del Sur S.L.", null, "Gestionada");
        contexto.Empresas.AddRange(clientePropio, clienteAjeno, empresaPropia, subcontrata);
        await contexto.SaveChangesAsync();

        var centroPropio = new Centro(clientePropio.Id, empresaPropia.Id, "Planta Norte");
        var centroAjeno = new Centro(clienteAjeno.Id, empresaPropia.Id, "Centro de un cliente sin relación con la subcontrata");
        contexto.Centros.AddRange(centroPropio, centroAjeno);

        var tipoTrabajador = new TipoDocumento("TC2", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador);
        var tipoCliente = new TipoDocumento("Certificado del Cliente", null, aplicaVencimientoAutomatico: false, 2, AmbitoAplicacion.Cliente);
        contexto.TiposDocumento.AddRange(tipoTrabajador, tipoCliente);
        await contexto.SaveChangesAsync();

        contexto.RelacionesEmpresariales.Add(RelacionEmpresarial.Crear(subcontrata.Id, clientePropio.Id, DateTime.UtcNow.AddMonths(-3)));
        await contexto.SaveChangesAsync();

        _subcontrataId = subcontrata.Id;
        _centroPropioId = centroPropio.Id;
        _centroAjenoId = centroAjeno.Id;
        _tipoTrabajadorId = tipoTrabajador.Id;
        _tipoClienteId = tipoCliente.Id;
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Falla_cuando_el_centro_no_tiene_relacion_con_la_subcontrata()
    {
        var resultado = await EjecutarAsync(_centroAjenoId, _tipoTrabajadorId);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("VerificacionExterna.CentroNoEncontrado");

        await using var contexto = CrearContexto();
        (await contexto.VerificacionesExternaSubcontrata.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Falla_cuando_el_tipo_documento_no_es_de_ambito_trabajador_ni_empresa()
    {
        var resultado = await EjecutarAsync(_centroPropioId, _tipoClienteId);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("VerificacionExterna.TipoNoEncontrado");
    }

    [Fact]
    public async Task Registra_la_verificacion_cuando_el_centro_tiene_relacion_vigente_y_el_tipo_es_valido()
    {
        var resultado = await EjecutarAsync(_centroPropioId, _tipoTrabajadorId);

        resultado.EsExitoso.Should().BeTrue();

        await using var contexto = CrearContexto();
        var verificacion = await contexto.VerificacionesExternaSubcontrata.SingleAsync();
        verificacion.SubcontrataId.Should().Be(_subcontrataId);
        verificacion.CentroId.Should().Be(_centroPropioId);
        verificacion.TipoDocumentoId.Should().Be(_tipoTrabajadorId);
    }

    private async Task<CaeManager.Domain.Common.Result> EjecutarAsync(Guid centroId, Guid tipoDocumentoId)
    {
        await using var contexto = CrearContexto();
        var handler = new RegistrarVerificacionExternaSubcontrataCommandHandler(
            new EmpresaRepository(contexto),
            new VerificacionExternaSubcontrataRepository(contexto),
            contexto, contexto, contexto,
            new AlcanceDatosServiceFalso(),
            new CurrentUserServiceFalso(Guid.NewGuid()),
            new AlmacenamientoFalso(),
            contexto);

        return await handler.Handle(
            new RegistrarVerificacionExternaSubcontrataCommand(
                _subcontrataId, centroId, tipoDocumentoId, new DateOnly(2026, 1, 1), ResultadoVerificacionExterna.Valido, null, null),
            CancellationToken.None);
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

    private sealed class AlmacenamientoFalso : CaeManager.Application.Common.IFileStorageService
    {
        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Ningún test de este fichero adjunta evidencia.");

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
