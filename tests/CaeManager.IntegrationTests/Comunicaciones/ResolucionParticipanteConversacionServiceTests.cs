using CaeManager.Application.Comunicaciones;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Comunicaciones;

/// <summary>
/// Antes de esta pieza, ParticipanteConversacion.EntidadRelacionadaId nunca se rellenaba en
/// ningún camino de ingesta real — todo participante nacía Desconocido (ronda de reducción de
/// ruido en Comunicaciones, ver docs de la sesión). Cubre el matching por email acotado al
/// Cliente de la conversación.
/// </summary>
public class ResolucionParticipanteConversacionServiceTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _clienteId;
    private Guid _centroId;
    private Guid _trabajadorActivoId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var cliente = new Cliente("Cliente Resolución Participante S.L.", "B10380194", esCritico: false);
        var empresa = new Empresa("Empresa Resolución Participante S.L.", "B10380186");
        contexto.Clientes.Add(cliente);
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro Resolución Participante");
        var trabajadorActivo = Trabajador.DeEmpresa(empresa.Id, "Ana", "García", "11111111H", email: "ana.garcia@trabajador.com");
        var trabajadorSinAsignacion = Trabajador.DeEmpresa(empresa.Id, "Luis", "Pérez", "22222222J", email: "luis.perez@trabajador.com");
        contexto.Centros.Add(centro);
        contexto.Trabajadores.AddRange(trabajadorActivo, trabajadorSinAsignacion);
        await contexto.SaveChangesAsync();

        contexto.Asignaciones.Add(new Asignacion(trabajadorActivo.Id, centro.Id, DateOnly.FromDateTime(DateTime.UtcNow)));
        await contexto.SaveChangesAsync();

        _clienteId = cliente.Id;
        _centroId = centro.Id;
        _trabajadorActivoId = trabajadorActivo.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private ResolucionParticipanteConversacionService CrearServicio()
    {
        var contexto = CrearContexto();
        return new ResolucionParticipanteConversacionService(contexto, contexto, contexto);
    }

    [Fact]
    public async Task Resuelve_el_email_de_un_trabajador_con_asignacion_activa_en_el_cliente()
    {
        var servicio = CrearServicio();

        var (tipoOrigen, entidadRelacionadaId) = await servicio.ResolverAsync("ana.garcia@trabajador.com", _clienteId);

        tipoOrigen.Should().Be(TipoParticipanteOrigen.Trabajador);
        entidadRelacionadaId.Should().Be(_trabajadorActivoId);
    }

    [Fact]
    public async Task El_matching_no_distingue_mayusculas()
    {
        var servicio = CrearServicio();

        var (tipoOrigen, entidadRelacionadaId) = await servicio.ResolverAsync("ANA.GARCIA@TRABAJADOR.COM", _clienteId);

        tipoOrigen.Should().Be(TipoParticipanteOrigen.Trabajador);
        entidadRelacionadaId.Should().Be(_trabajadorActivoId);
    }

    [Fact]
    public async Task Un_email_sin_ningun_trabajador_coincidente_queda_desconocido()
    {
        var servicio = CrearServicio();

        var (tipoOrigen, entidadRelacionadaId) = await servicio.ResolverAsync("nadie@ejemplo.com", _clienteId);

        tipoOrigen.Should().Be(TipoParticipanteOrigen.Desconocido);
        entidadRelacionadaId.Should().BeNull();
    }

    [Fact]
    public async Task Un_trabajador_sin_asignacion_activa_no_se_resuelve()
    {
        var servicio = CrearServicio();

        var (tipoOrigen, entidadRelacionadaId) = await servicio.ResolverAsync("luis.perez@trabajador.com", _clienteId);

        tipoOrigen.Should().Be(TipoParticipanteOrigen.Desconocido);
        entidadRelacionadaId.Should().BeNull();
    }

    [Fact]
    public async Task Sin_cliente_resuelto_queda_desconocido_sin_consultar_nada()
    {
        var servicio = CrearServicio();

        var (tipoOrigen, entidadRelacionadaId) = await servicio.ResolverAsync("ana.garcia@trabajador.com", clienteId: null);

        tipoOrigen.Should().Be(TipoParticipanteOrigen.Desconocido);
        entidadRelacionadaId.Should().BeNull();
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
