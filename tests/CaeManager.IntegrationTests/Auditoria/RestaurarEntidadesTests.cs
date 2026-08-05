using CaeManager.Application.Centros.Commands.RestaurarCentro;
using CaeManager.Application.Clientes.Commands.RestaurarCliente;
using CaeManager.Application.Documentos.Commands.RestaurarDocumento;
using CaeManager.Application.Empresas.Commands.RestaurarEmpresa;
using CaeManager.Application.Trabajadores.Commands.RestaurarTrabajador;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Centro = CaeManager.Domain.Centros.Centro;

namespace CaeManager.IntegrationTests.Auditoria;

/// <summary>
/// Fase D ("Deshacer al eliminar"): <c>EntidadBase.Restaurar()</c> vivía sin
/// usar en el dominio — estos son los primeros tests que ejercitan el
/// camino completo Command → IgnoreQueryFilters() → Restaurar() →
/// SaveChanges, contra PostgreSQL real. Cada entidad lleva dos casos: se
/// restaura de verdad, y un Id de otro tenant nunca se restaura — la
/// frontera de seguridad que <c>IgnoreQueryFilters()</c> podría romper si
/// no se acotara el TenantId a mano (ver el comentario de
/// RestaurarClienteCommand).
/// </summary>
public class RestaurarEntidadesTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otroTenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(_tenant);
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Restaura_un_cliente_eliminado()
    {
        Guid clienteId;
        await using (var contexto = CrearContexto(_tenant))
        {
            var cliente = new Cliente("Restaurar S.L.", "B12345674", esCritico: false);
            contexto.Clientes.Add(cliente);
            await contexto.SaveChangesAsync();
            cliente.MarcarComoEliminado(Guid.NewGuid());
            await contexto.SaveChangesAsync();
            clienteId = cliente.Id;
        }

        await using var contextoRestaurar = CrearContexto(_tenant);
        var handler = new RestaurarClienteCommandHandler(
            contextoRestaurar, new TenantActualAmbiental { TenantId = _tenant }, contextoRestaurar);

        var resultado = await handler.Handle(new RestaurarClienteCommand(clienteId), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        (await contextoRestaurar.Clientes.SingleAsync(c => c.Id == clienteId)).EstaEliminado.Should().BeFalse();
    }

    [Fact]
    public async Task Un_cliente_eliminado_de_otro_tenant_no_se_restaura()
    {
        Guid clienteId;
        await using (var contexto = CrearContexto(_tenant))
        {
            var cliente = new Cliente("Cliente Ajeno S.L.", "B12345674", esCritico: false);
            contexto.Clientes.Add(cliente);
            await contexto.SaveChangesAsync();
            cliente.MarcarComoEliminado(Guid.NewGuid());
            await contexto.SaveChangesAsync();
            clienteId = cliente.Id;
        }

        await using var contextoAtacante = CrearContexto(_otroTenant);
        var handler = new RestaurarClienteCommandHandler(
            contextoAtacante, new TenantActualAmbiental { TenantId = _otroTenant }, contextoAtacante);

        var resultado = await handler.Handle(new RestaurarClienteCommand(clienteId), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
    }

    [Fact]
    public async Task Restaura_una_empresa_eliminada()
    {
        Guid empresaId;
        await using (var contexto = CrearContexto(_tenant))
        {
            var empresa = new Empresa("Restaurar Empresa S.L.", "B87654323");
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();
            empresa.MarcarComoEliminado(Guid.NewGuid());
            await contexto.SaveChangesAsync();
            empresaId = empresa.Id;
        }

        await using var contextoRestaurar = CrearContexto(_tenant);
        var handler = new RestaurarEmpresaCommandHandler(
            contextoRestaurar, new TenantActualAmbiental { TenantId = _tenant }, contextoRestaurar);

        var resultado = await handler.Handle(new RestaurarEmpresaCommand(empresaId), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        (await contextoRestaurar.Empresas.SingleAsync(e => e.Id == empresaId)).EstaEliminado.Should().BeFalse();
    }

    [Fact]
    public async Task Una_empresa_eliminada_de_otro_tenant_no_se_restaura()
    {
        Guid empresaId;
        await using (var contexto = CrearContexto(_tenant))
        {
            var empresa = new Empresa("Empresa Ajena S.L.", "B87654323");
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();
            empresa.MarcarComoEliminado(Guid.NewGuid());
            await contexto.SaveChangesAsync();
            empresaId = empresa.Id;
        }

        await using var contextoAtacante = CrearContexto(_otroTenant);
        var handler = new RestaurarEmpresaCommandHandler(
            contextoAtacante, new TenantActualAmbiental { TenantId = _otroTenant }, contextoAtacante);

        var resultado = await handler.Handle(new RestaurarEmpresaCommand(empresaId), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
    }

    [Fact]
    public async Task Restaura_un_centro_eliminado()
    {
        Guid centroId;
        await using (var contexto = CrearContexto(_tenant))
        {
            var cliente = new Cliente("Cliente del Centro S.L.", "B12345674", esCritico: false);
            var empresa = new Empresa("Empresa del Centro S.L.", "B87654323");
            contexto.Clientes.Add(cliente);
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();

            var centro = new Centro(cliente.Id, empresa.Id, "Centro a restaurar");
            contexto.Centros.Add(centro);
            await contexto.SaveChangesAsync();
            centro.MarcarComoEliminado(Guid.NewGuid());
            await contexto.SaveChangesAsync();
            centroId = centro.Id;
        }

        await using var contextoRestaurar = CrearContexto(_tenant);
        var handler = new RestaurarCentroCommandHandler(
            contextoRestaurar, new TenantActualAmbiental { TenantId = _tenant }, contextoRestaurar);

        var resultado = await handler.Handle(new RestaurarCentroCommand(centroId), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        (await contextoRestaurar.Centros.SingleAsync(c => c.Id == centroId)).EstaEliminado.Should().BeFalse();
    }

    [Fact]
    public async Task Un_centro_eliminado_de_otro_tenant_no_se_restaura()
    {
        Guid centroId;
        await using (var contexto = CrearContexto(_tenant))
        {
            var cliente = new Cliente("Cliente del Centro Ajeno S.L.", "B12345674", esCritico: false);
            var empresa = new Empresa("Empresa del Centro Ajeno S.L.", "B87654323");
            contexto.Clientes.Add(cliente);
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();

            var centro = new Centro(cliente.Id, empresa.Id, "Centro ajeno");
            contexto.Centros.Add(centro);
            await contexto.SaveChangesAsync();
            centro.MarcarComoEliminado(Guid.NewGuid());
            await contexto.SaveChangesAsync();
            centroId = centro.Id;
        }

        await using var contextoAtacante = CrearContexto(_otroTenant);
        var handler = new RestaurarCentroCommandHandler(
            contextoAtacante, new TenantActualAmbiental { TenantId = _otroTenant }, contextoAtacante);

        var resultado = await handler.Handle(new RestaurarCentroCommand(centroId), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
    }

    [Fact]
    public async Task Restaura_un_trabajador_eliminado()
    {
        Guid trabajadorId;
        await using (var contexto = CrearContexto(_tenant))
        {
            var empresa = new Empresa("Empresa del Trabajador S.L.", "B87654323");
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();

            var trabajador = Trabajador.DeEmpresa(empresa.Id, "Restaurar", "Trabajador", "77189989B");
            contexto.Trabajadores.Add(trabajador);
            await contexto.SaveChangesAsync();
            trabajador.MarcarComoEliminado(Guid.NewGuid());
            await contexto.SaveChangesAsync();
            trabajadorId = trabajador.Id;
        }

        await using var contextoRestaurar = CrearContexto(_tenant);
        var handler = new RestaurarTrabajadorCommandHandler(
            contextoRestaurar, new TenantActualAmbiental { TenantId = _tenant }, contextoRestaurar);

        var resultado = await handler.Handle(new RestaurarTrabajadorCommand(trabajadorId), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        (await contextoRestaurar.Trabajadores.SingleAsync(t => t.Id == trabajadorId)).EstaEliminado.Should().BeFalse();
    }

    [Fact]
    public async Task Un_trabajador_eliminado_de_otro_tenant_no_se_restaura()
    {
        Guid trabajadorId;
        await using (var contexto = CrearContexto(_tenant))
        {
            var empresa = new Empresa("Empresa del Trabajador Ajeno S.L.", "B87654323");
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();

            var trabajador = Trabajador.DeEmpresa(empresa.Id, "Ajeno", "Trabajador", "12345678Z");
            contexto.Trabajadores.Add(trabajador);
            await contexto.SaveChangesAsync();
            trabajador.MarcarComoEliminado(Guid.NewGuid());
            await contexto.SaveChangesAsync();
            trabajadorId = trabajador.Id;
        }

        await using var contextoAtacante = CrearContexto(_otroTenant);
        var handler = new RestaurarTrabajadorCommandHandler(
            contextoAtacante, new TenantActualAmbiental { TenantId = _otroTenant }, contextoAtacante);

        var resultado = await handler.Handle(new RestaurarTrabajadorCommand(trabajadorId), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
    }

    [Fact]
    public async Task Restaura_un_documento_eliminado()
    {
        Guid documentoId;
        await using (var contexto = CrearContexto(_tenant))
        {
            var empresa = new Empresa("Empresa del Documento S.L.", "B87654323");
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();

            var trabajador = Trabajador.DeEmpresa(empresa.Id, "Documento", "Trabajador", "77189989B");
            contexto.Trabajadores.Add(trabajador);

            var tipoDocumento = new TipoDocumento("Apto médico", 12, true, 1, AmbitoAplicacion.Trabajador, esObligatorio: true);
            contexto.TiposDocumento.Add(tipoDocumento);
            await contexto.SaveChangesAsync();

            var documento = Documento.DeTrabajador(
                trabajador.Id, tipoDocumento.Id, DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1));
            contexto.Documentos.Add(documento);
            await contexto.SaveChangesAsync();
            documento.MarcarComoEliminado(Guid.NewGuid());
            await contexto.SaveChangesAsync();
            documentoId = documento.Id;
        }

        await using var contextoRestaurar = CrearContexto(_tenant);
        var handler = new RestaurarDocumentoCommandHandler(
            contextoRestaurar, new TenantActualAmbiental { TenantId = _tenant }, contextoRestaurar);

        var resultado = await handler.Handle(new RestaurarDocumentoCommand(documentoId), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        (await contextoRestaurar.Documentos.SingleAsync(d => d.Id == documentoId)).EstaEliminado.Should().BeFalse();
    }

    [Fact]
    public async Task Un_documento_eliminado_de_otro_tenant_no_se_restaura()
    {
        Guid documentoId;
        await using (var contexto = CrearContexto(_tenant))
        {
            var empresa = new Empresa("Empresa del Documento Ajeno S.L.", "B87654323");
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();

            var trabajador = Trabajador.DeEmpresa(empresa.Id, "Ajeno", "Documento", "12345678Z");
            contexto.Trabajadores.Add(trabajador);

            var tipoDocumento = new TipoDocumento("Apto médico", 12, true, 1, AmbitoAplicacion.Trabajador, esObligatorio: true);
            contexto.TiposDocumento.Add(tipoDocumento);
            await contexto.SaveChangesAsync();

            var documento = Documento.DeTrabajador(
                trabajador.Id, tipoDocumento.Id, DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1));
            contexto.Documentos.Add(documento);
            await contexto.SaveChangesAsync();
            documento.MarcarComoEliminado(Guid.NewGuid());
            await contexto.SaveChangesAsync();
            documentoId = documento.Id;
        }

        await using var contextoAtacante = CrearContexto(_otroTenant);
        var handler = new RestaurarDocumentoCommandHandler(
            contextoAtacante, new TenantActualAmbiental { TenantId = _otroTenant }, contextoAtacante);

        var resultado = await handler.Handle(new RestaurarDocumentoCommand(documentoId), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
    }

    [Fact]
    public async Task No_hace_nada_si_la_entidad_no_estaba_eliminada()
    {
        Guid clienteId;
        await using (var contexto = CrearContexto(_tenant))
        {
            var cliente = new Cliente("Nunca Eliminado S.L.", "B12345674", esCritico: false);
            contexto.Clientes.Add(cliente);
            await contexto.SaveChangesAsync();
            clienteId = cliente.Id;
        }

        await using var contextoRestaurar = CrearContexto(_tenant);
        var handler = new RestaurarClienteCommandHandler(
            contextoRestaurar, new TenantActualAmbiental { TenantId = _tenant }, contextoRestaurar);

        var resultado = await handler.Handle(new RestaurarClienteCommand(clienteId), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
    }

    private CaeManagerDbContext CrearContexto(Guid tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
