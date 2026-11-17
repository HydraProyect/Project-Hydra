using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Domain.Operaciones;
using CaeManager.Web.Components.Layout;
using FluentAssertions;
using MediatR;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Precondición de fusión de la PR #2313 (D-4): cuando el Gestor CAE elige en el selector la entrada Inbound de un Tenant
/// beneficiario al que solo llega por una Asignación de Operación Inbound, los lectores del Tenant activo de la interfaz
/// pintan ese Tenant, y no el Tenant de origen ni «Selecciona una empresa». Todos leen la misma fuente,
/// <see cref="ObtenerClientesAutorizadosQuery.ParaTenantActivo"/>; el handler real se prueba en
/// <see cref="ObtenerClientesAutorizadosPorPapelTests"/>. Aquí, el lector que compone la cabecera de las pantallas de un
/// Tenant a la vez y la guarda de que ningún lector del Tenant activo vuelva a la lista por Tenant.
/// </summary>
public class LectoresTenantActivoInboundTests
{
    private static readonly Guid Origen = Guid.NewGuid();
    private static readonly Guid Norte = Guid.NewGuid();
    private static readonly Guid Este = Guid.NewGuid();
    private static readonly Guid Sur = Guid.NewGuid();

    private static readonly ClienteAutorizadoDto Propio = new(Origen, "Operador CAE externo", EsOrigen: true);
    private static readonly ClienteAutorizadoDto NorteOutbound =
        new(Norte, "Empresa Norte", EsOrigen: false, EsGestionadoPorOperacion: true, EsCarteraGestorCae: true);
    private static readonly ClienteAutorizadoDto EsteOutbound =
        new(Este, "Empresa Este", EsOrigen: false, EsGestionadoPorOperacion: true, EsCarteraGestorCae: true);
    private static readonly ClienteAutorizadoDto SurInbound =
        new(Sur, "Empresa Sur", EsOrigen: false, EsGestionadoPorOperacion: true, EsCarteraGestorCae: true)
        { Servicio = ServicioCae.Inbound };

    /// <summary>La lista por Tenant a cualquier lectura y, solo a <see cref="ObtenerClientesAutorizadosQuery.ParaTenantActivo"/>, la del handler.</summary>
    private sealed class Mediador(IReadOnlyList<ClienteAutorizadoDto> porTenant, IReadOnlyList<ClienteAutorizadoDto> paraTenantActivo)
        : IMediator
    {
        public Task<T> Send<T>(IRequest<T> request, CancellationToken cancellationToken = default) =>
            request is ObtenerClientesAutorizadosQuery q
                ? Task.FromResult((T)(object)(q.ParaTenantActivo ? paraTenantActivo : porTenant))
                : throw new NotSupportedException(request.GetType().Name);

        public Task Send<T>(T request, CancellationToken cancellationToken = default) where T : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<T> CreateStream<T>(IStreamRequest<T> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<T>(T notification, CancellationToken cancellationToken = default) where T : INotification => Task.CompletedTask;
    }

    [Fact]
    public async Task La_cabecera_de_un_Tenant_a_la_vez_pinta_el_Tenant_elegido_con_la_entrada_Inbound()
    {
        var mediador = new Mediador([Propio, NorteOutbound], [Propio, NorteOutbound, SurInbound]);

        var contexto = await ContextoEmpresaActiva.ResolverAsync(mediador, new TenantActualFijo(Sur));

        contexto.SinSeleccion.Should().BeFalse("con Empresa Sur activa no se pide elegir empresa");
        contexto.Activa!.Nombre.Should().Be("Empresa Sur");
    }

    [Fact]
    public async Task Con_el_Tenant_alcanzado_solo_por_Inbound_como_unico_externo_la_cabecera_lo_pinta()
    {
        // Caso puro de D-4: sin otro Tenant gestionado, solo la entrada Inbound hace visible el selector.
        var mediador = new Mediador([Propio], [Propio, SurInbound]);

        (await ContextoEmpresaActiva.ResolverAsync(mediador, new TenantActualFijo(Sur)))
            .Should().Be(new ContextoEmpresaActiva(SurInbound, false));
    }

    [Fact]
    public async Task Con_una_entrada_Outbound_la_cabecera_no_cambia()
    {
        IReadOnlyList<ClienteAutorizadoDto> porTenant = [Propio, NorteOutbound, EsteOutbound];
        var mediador = new Mediador(porTenant, porTenant);

        var contexto = await ContextoEmpresaActiva.ResolverAsync(mediador, new TenantActualFijo(Norte));

        contexto.Should().Be(new ContextoEmpresaActiva(NorteOutbound, false));
        (await ContextoEmpresaActiva.ResolverAsync(mediador, new TenantActualFijo(Origen)))
            .Should().Be(new ContextoEmpresaActiva(null, true), "el estado 4a del origen sigue igual");
    }

    /// <summary>
    /// Ficheros de la interfaz que resuelven el Tenant activo con <see cref="ClientesAutorizados.Activo"/>,
    /// <see cref="ClientesAutorizados.PideElegirEmpresa"/> o <see cref="ClientesAutorizados.SinEmpresaSeleccionada"/>.
    /// El selector de la cabecera queda fuera: compone su propia lista con <see cref="ObtenerClientesAutorizadosQuery.PorPapel"/>.
    /// Límite del instrumento: compara texto por fichero y solo en <c>src/CaeManager.Web</c>; no ve un lector que resuelva el
    /// Tenant activo a mano ni uno que mezcle en el mismo fichero la lista para el Tenant activo con otra lista.
    /// </summary>
    private static readonly string[] Lectores =
    [
        "Components/Layout/ContextoEmpresaActiva.cs",
        "Components/Layout/EnlaceProfundoOtraEmpresa.razor",
        "Components/Layout/FichaNoEncontrada.razor",
        "Components/Workspace/PanelNoEncontrado.razor",
        "Features/AsistenteIa/AsistenteIa.razor.cs",
        "Features/BusquedaGlobal/BuscadorGlobal.razor.cs",
        "Features/Centros/CentrosEndpoints.cs",
        "Features/Clientes/ClientesEndpoints.cs",
        "Features/Documentos/DocumentosEndpoints.cs",
        "Features/Empresas/EmpresasEndpoints.cs",
        "Features/Proyectos/ProyectosEndpoints.cs",
        "Features/Subcontratas/SubcontratasEndpoints.cs",
        "Features/Trabajadores/TrabajadoresEndpoints.cs",
        "Features/Vehiculos/VehiculosEndpoints.cs",
    ];

    [Fact]
    public void Todos_los_lectores_del_Tenant_activo_leen_la_lista_para_el_Tenant_activo()
    {
        var web = Path.Combine(RaizRepositorio(), "src", "CaeManager.Web");
        string[] resolutores = ["ClientesAutorizados.Activo(", "ClientesAutorizados.PideElegirEmpresa(", "ClientesAutorizados.SinEmpresaSeleccionada("];

        var encontrados = Directory.EnumerateFiles(web, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.EndsWith("SelectorClienteActivo.razor.cs", StringComparison.Ordinal))
            .Where(f => resolutores.Any(File.ReadAllText(f).Contains))
            .Select(f => Path.GetRelativePath(web, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

        encontrados.Should().Equal(Lectores.Order(StringComparer.Ordinal),
            "un lector nuevo del Tenant activo entra en el inventario de la PR y en esta lista");
        foreach (var lector in encontrados)
        {
            var fuente = File.ReadAllText(Path.Combine(web, lector));
            fuente.Should().Contain("new ObtenerClientesAutorizadosQuery { ParaTenantActivo = true }", lector);
            fuente.Should().NotContain("new ObtenerClientesAutorizadosQuery()",
                $"{lector} caería al Tenant de origen con un Tenant alcanzado solo por Inbound");
        }
    }

    private static string RaizRepositorio()
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null && !File.Exists(Path.Combine(directorio.FullName, "CaeManager.slnx")))
            directorio = directorio.Parent;
        directorio.Should().NotBeNull("el test necesita la raíz del repositorio (CaeManager.slnx)");
        return directorio!.FullName;
    }
}
