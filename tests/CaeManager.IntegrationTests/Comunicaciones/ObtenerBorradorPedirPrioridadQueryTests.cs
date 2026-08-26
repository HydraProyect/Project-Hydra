using CaeManager.Application.Comunicaciones.Queries.ObtenerBorradorPedirPrioridad;
using CaeManager.Application.Alertas;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Comunicaciones;
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

namespace CaeManager.IntegrationTests.Comunicaciones;

/// <summary>
/// Fase G ("Pedir prioridad"): el borrador reutiliza
/// <see cref="IDocumentosFaltantesService"/> (misma fuente que la Bandeja del
/// gestor y el preflight de asignación en lote) para construir el correo, y
/// resuelve destinatario/conexión con las mismas reglas que
/// <c>PedirPrioridadValidacionCommand</c> aplicará al enviar de verdad.
/// </summary>
public class ObtenerBorradorPedirPrioridadQueryTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    private Guid _clienteId;
    private Guid _centroId;
    private Guid _centroAjenoId;
    private Guid _trabajadorId;
    private Guid _tipoObligatorioId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var cliente = Empresa.CrearComoCliente("Pedir Prioridad S.L.", "B12345674", false, null, null);
        var empresa = new Empresa("Empresa Prioridad S.L.", "B87654323");
        contexto.Empresas.Add(cliente);
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro con pendientes");
        var centroAjeno = new Centro(cliente.Id, empresa.Id, "Centro ajeno a la cartera");
        contexto.Centros.AddRange(centro, centroAjeno);

        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Ana", "García", "77189989B");
        contexto.Trabajadores.Add(trabajador);

        var tipoObligatorio = new TipoDocumento("Apto médico", 12, true, 1, AmbitoAplicacion.Trabajador, esObligatorio: true);
        contexto.TiposDocumento.Add(tipoObligatorio);
        await contexto.SaveChangesAsync();

        contexto.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, DateOnly.FromDateTime(DateTime.UtcNow)));
        await contexto.SaveChangesAsync();

        _clienteId = cliente.Id;
        _centroId = centro.Id;
        _centroAjenoId = centroAjeno.Id;
        _trabajadorId = trabajador.Id;
        _tipoObligatorioId = tipoObligatorio.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private ObtenerBorradorPedirPrioridadQueryHandler CrearHandler(CaeManagerDbContext contexto, AlcanceDatosServiceFalso? alcance = null) =>
        new(contexto, contexto, contexto, contexto, contexto, contexto, contexto, contexto,
            new DocumentosFaltantesService(contexto, contexto), alcance ?? new AlcanceDatosServiceFalso());

    [Fact]
    public async Task Sin_trabajadores_asignados_devuelve_fallo()
    {
        await using var contexto = CrearContexto();
        var handler = CrearHandler(contexto);

        var resultado = await handler.Handle(new ObtenerBorradorPedirPrioridadQuery(_centroAjenoId), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("SolicitudPrioridad.SinTrabajadores");
    }

    [Fact]
    public async Task Sin_documentos_pendientes_devuelve_fallo()
    {
        await using (var contexto = CrearContexto())
        {
            contexto.Documentos.Add(Documento.DeTrabajador(
                _trabajadorId, _tipoObligatorioId, DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1)));
            await contexto.SaveChangesAsync();
        }

        await using var contexto2 = CrearContexto();
        var handler = CrearHandler(contexto2);

        var resultado = await handler.Handle(new ObtenerBorradorPedirPrioridadQuery(_centroId), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("SolicitudPrioridad.SinPendientes");
    }

    [Fact]
    public async Task Con_documentos_pendientes_construye_el_borrador_con_el_total_correcto()
    {
        await using var contexto = CrearContexto();
        var handler = CrearHandler(contexto);

        var resultado = await handler.Handle(new ObtenerBorradorPedirPrioridadQuery(_centroId), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.TotalDocumentosPendientes.Should().Be(1);
        resultado.Valor.CuerpoHtml.Should().Contain("Ana García").And.Contain("Apto médico");
        resultado.Valor.Asunto.Should().Contain("Centro con pendientes");
        resultado.Valor.HayConexionDisponible.Should().BeFalse();
        resultado.Valor.UltimaSolicitudEnviadaEnUtc.Should().BeNull();
    }

    [Fact]
    public async Task Resuelve_el_destinatario_desde_el_canal_de_gestion_por_correo()
    {
        await using (var contexto = CrearContexto())
        {
            contexto.CanalesGestionDocumental.Add(
                CanalGestionDocumental.PorEmail(_centroId, "Gestión general", "prl@centro.com", "Responsable PRL"));
            await contexto.SaveChangesAsync();
        }

        await using var contexto2 = CrearContexto();
        var resultado = await CrearHandler(contexto2).Handle(new ObtenerBorradorPedirPrioridadQuery(_centroId), CancellationToken.None);

        resultado.Valor.DestinatarioSugerido.Should().Be("prl@centro.com");
    }

    [Fact]
    public async Task Sin_canal_de_correo_usa_el_contacto_del_centro_solo_si_parece_un_email()
    {
        await using var contexto = CrearContexto();
        var centro = await contexto.Centros.FirstAsync(c => c.Id == _centroId);
        centro.Actualizar(centro.Nombre, centro.CodigoCentro, centro.Direccion, "no-es-un-email", centro.ContratoVigenteHasta);
        await contexto.SaveChangesAsync();

        var resultado = await CrearHandler(contexto).Handle(new ObtenerBorradorPedirPrioridadQuery(_centroId), CancellationToken.None);

        resultado.Valor.DestinatarioSugerido.Should().BeNull();
    }

    [Fact]
    public async Task Prefiere_la_conexion_especifica_del_cliente_sobre_la_del_tenant()
    {
        await using (var contexto = CrearContexto())
        {
            contexto.ConexionesIntegracion.Add(new ConexionIntegracion("tenant@cae.com", "Buzón del tenant"));
            contexto.ConexionesIntegracion.Add(new ConexionIntegracion("cliente@cae.com", "Buzón del cliente", _clienteId));
            await contexto.SaveChangesAsync();
        }

        await using var contexto2 = CrearContexto();
        var resultado = await CrearHandler(contexto2).Handle(new ObtenerBorradorPedirPrioridadQuery(_centroId), CancellationToken.None);

        resultado.Valor.HayConexionDisponible.Should().BeTrue();
    }

    [Fact]
    public async Task Fuera_de_la_cartera_visible_devuelve_fallo()
    {
        await using var contexto = CrearContexto();
        var handler = CrearHandler(contexto, new AlcanceDatosServiceFalso(centroIds: [_centroAjenoId]));

        var resultado = await handler.Handle(new ObtenerBorradorPedirPrioridadQuery(_centroId), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Centro.NoEncontrado");
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
