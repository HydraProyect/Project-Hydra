using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Reclamaciones.Queries.ObtenerUltimaReclamacionCliente;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Reclamaciones;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Reclamaciones;

/// <summary>
/// La «última reclamación» de Centro 360 ve las dos vías de «Gestionar» (decisión de Chris, 2026-10-18): la anclada al
/// Cliente (Subcontrata) y la enviada a la Empresa empleadora de sus Trabajadores. Contra Postgres real, porque la vía de
/// la Empresa se resuelve con subconsultas (Centro → Asignación → línea de la reclamación → Documento del Trabajador).
/// </summary>
public class UltimaReclamacionClienteViaEmpresaTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _clienteId;
    private Guid _empresaId;
    private Guid _otraEmpresaId;
    private Guid _documentoId;
    private Guid _documentoDeOtroClienteId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var cliente = Empresa.CrearComoCliente("Cliente Centro 360 S.L.", "B12345674", false, null, null);
        var otroCliente = Empresa.CrearComoCliente("Otro Cliente S.L.", "B10380186", false, null, null);
        var empresa = new Empresa("Contratista Centro 360 S.L.", "B87654323");
        var otraEmpresa = new Empresa("Otra Contratista S.L.", "B10380194");
        contexto.Empresas.AddRange(cliente, otroCliente, empresa, otraEmpresa);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro del Cliente");
        var centroDelOtro = new Centro(otroCliente.Id, otraEmpresa.Id, "Centro del otro Cliente");
        contexto.Centros.AddRange(centro, centroDelOtro);
        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Ana", "Pérez", "77189989B");
        var trabajadorDelOtro = Trabajador.DeEmpresa(otraEmpresa.Id, "Eva", "Gil", "00000000T");
        contexto.Trabajadores.AddRange(trabajador, trabajadorDelOtro);
        var tipo = new TipoDocumento("Ficha de riesgos", 12, aplicaVencimientoAutomatico: true, 1, AmbitoAplicacion.Trabajador);
        contexto.TiposDocumento.Add(tipo);
        await contexto.SaveChangesAsync();

        contexto.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, DiaDeNegocio.Hoy()));
        contexto.Asignaciones.Add(new Asignacion(trabajadorDelOtro.Id, centroDelOtro.Id, DiaDeNegocio.Hoy()));
        var documento = Documento.DeTrabajador(trabajador.Id, tipo.Id, DiaDeNegocio.Hoy().AddMonths(-10), VigenciaDocumento.VenceEl(DiaDeNegocio.Hoy().AddMonths(1)));
        var documentoDelOtro = Documento.DeTrabajador(trabajadorDelOtro.Id, tipo.Id, DiaDeNegocio.Hoy().AddMonths(-10), VigenciaDocumento.VenceEl(DiaDeNegocio.Hoy().AddMonths(1)));
        contexto.Documentos.AddRange(documento, documentoDelOtro);
        await contexto.SaveChangesAsync();

        _clienteId = cliente.Id;
        _empresaId = empresa.Id;
        _otraEmpresaId = otraEmpresa.Id;
        _documentoId = documento.Id;
        _documentoDeOtroClienteId = documentoDelOtro.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task La_reclamacion_a_la_Empresa_empleadora_es_la_ultima_del_Cliente_aunque_a_el_no_se_le_reclamase_nada()
    {
        var conversacion = await SembrarAsync(ReclamacionDocumental.ParaEmpresaEmpleadora(
            _empresaId, Guid.NewGuid(), "agenda@contratista.test", DateTime.UtcNow.AddDays(-2), [_documentoId]));

        var ultima = await EjecutarAsync(_clienteId);

        ultima.Should().NotBeNull();
        ultima!.TotalDocumentos.Should().Be(1);
        ultima.Id.Should().Be(conversacion.Id);
    }

    [Fact]
    public async Task Gana_la_mas_reciente_de_las_dos_vias()
    {
        await SembrarAsync(ReclamacionDocumental.ParaCliente(
            _clienteId, Guid.NewGuid(), "compras@cliente.test", DateTime.UtcNow.AddDays(-9), [Guid.NewGuid()]));
        var viaEmpresa = await SembrarAsync(ReclamacionDocumental.ParaEmpresaEmpleadora(
            _empresaId, Guid.NewGuid(), "agenda@contratista.test", DateTime.UtcNow.AddDays(-3), [_documentoId]));

        (await EjecutarAsync(_clienteId))!.Id.Should().Be(viaEmpresa.Id);

        var alCliente = await SembrarAsync(ReclamacionDocumental.ParaCliente(
            _clienteId, Guid.NewGuid(), "compras@cliente.test", DateTime.UtcNow.AddDays(-1), [Guid.NewGuid()]));

        (await EjecutarAsync(_clienteId))!.Id.Should().Be(alCliente.Id);
    }

    [Fact]
    public async Task Lo_reclamado_a_otra_Empresa_por_Trabajadores_de_otro_Cliente_o_de_la_propia_Empresa_no_cuenta()
    {
        await SembrarAsync(ReclamacionDocumental.ParaEmpresaEmpleadora(
            _otraEmpresaId, Guid.NewGuid(), "agenda@otra.test", DateTime.UtcNow.AddDays(-1), [_documentoDeOtroClienteId]));
        await SembrarAsync(ReclamacionDocumental.ParaEmpresa(
            _empresaId, Guid.NewGuid(), "agenda@contratista.test", DateTime.UtcNow.AddDays(-1), [Guid.NewGuid()]));

        (await EjecutarAsync(_clienteId)).Should().BeNull();
    }

    [Fact]
    public async Task Sin_alcance_de_gestion_sobre_la_Empresa_su_reclamacion_no_aparece()
    {
        await SembrarAsync(ReclamacionDocumental.ParaEmpresaEmpleadora(
            _empresaId, Guid.NewGuid(), "agenda@contratista.test", DateTime.UtcNow.AddDays(-2), [_documentoId]));

        var ultima = await EjecutarAsync(_clienteId, new AlcanceDatosServiceFalso(empresaIdsParaGestion: [_otraEmpresaId]));

        ultima.Should().BeNull("quien no gestiona la Empresa no ve su rastro");
    }

    private async Task<ReclamacionDocumental> SembrarAsync(ReclamacionDocumental reclamacion)
    {
        await using var contexto = CrearContexto();
        contexto.ReclamacionesDocumentales.Add(reclamacion);
        await contexto.SaveChangesAsync();
        return reclamacion;
    }

    private async Task<UltimaReclamacionClienteDto?> EjecutarAsync(Guid clienteId, AlcanceDatosServiceFalso? alcance = null)
    {
        await using var lectura = CrearContexto();
        var handler = new ObtenerUltimaReclamacionClienteQueryHandler(
            lectura, alcance ?? new AlcanceDatosServiceFalso(),
            new DestinoDeReclamacionesEnviadasService(lectura, lectura, lectura, lectura, lectura, lectura));

        return await handler.Handle(new ObtenerUltimaReclamacionClienteQuery(clienteId), CancellationToken.None);
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
