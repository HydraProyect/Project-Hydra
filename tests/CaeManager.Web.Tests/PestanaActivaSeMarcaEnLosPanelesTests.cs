using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentroPorId;
using CaeManager.Application.Centros.Queries.ObtenerEstadoCentro;
using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.ObtenerCumplimientoEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Application.Reclamaciones.Queries.ObtenerUltimaReclamacionCliente;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadorPorId;
using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculoPorId;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Centros.Components;
using CaeManager.Web.Features.Clientes.Components;
using CaeManager.Web.Features.Empresas.Components;
using CaeManager.Web.Features.Trabajadores.Components;
using CaeManager.Web.Features.Vehiculos.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// DEFECTO 1 (visto el 2026-09-11 en Subcontrata 360, antes de su corrección):
/// <c>&lt;Pestanas PestanaActiva="PestanaActiva"&gt;</c> sin <c>@</c> le pasa a
/// <c>Pestanas</c> el LITERAL «PestanaActiva», nunca el valor real de la
/// pestaña activa — así que ninguna pestaña quedaba marcada
/// (<c>aria-selected="true"</c>), tampoco al abrir el panel por deep link. El
/// mismo patrón de tecleo estaba en Vehículo, Trabajador, Cliente, Empresa y
/// Centro (Subcontrata ya lo tenía corregido — ver <c>Subcontrata360Gen2Tests</c>).
///
/// <para>
/// Un test por panel, cada uno cae con la mutación que le quita el <c>@</c> a
/// <c>PestanaActiva="@PestanaActiva"</c> en el <c>.razor</c> correspondiente:
/// sin él, <c>Pestanas</c> vuelve a recibir el literal y ningún
/// <c>[role=tab]</c> tiene <c>aria-selected="true"</c>, así que
/// <c>cut.Find(...)</c> lanza por no encontrar ningún elemento.
/// </para>
///
/// <para>
/// Cada test carga solo lo mínimo que la pestaña "informacion" necesita — es
/// la única que no dispara una consulta adicional al entrar (ver
/// <c>OnParametersSetAsync</c> de cada panel) — para no acoplar este test a
/// las demás pestañas.
/// </para>
/// </summary>
public class PestanaActivaSeMarcaEnLosPanelesTests : BunitContext
{
    /// <summary>Un responder por test: nada compartido que un panel no toca necesita fingirse.</summary>
    private sealed class MediatorFalso(Func<object, object?> responder) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)responder(request)!);

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class UsuarioActualFalso : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class AlmacenArchivosQueNadieDebeTocar : IFileStorageService
    {
        private static Exception NoDeberia() =>
            new NotSupportedException("La pestaña \"informacion\" no abre ningún archivo; si esto salta, el test dejó de estar acotado a esa pestaña.");

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
    }

    private sealed class ResolucionProveedorQueNadieDebeTocar : IResolucionProveedorPlataformaCaeService
    {
        private static Exception NoDeberia() =>
            new NotSupportedException("La pestaña \"informacion\" no resuelve ningún proveedor de plataforma CAE; si esto salta, el test dejó de estar acotado a esa pestaña.");

        public Task<IReadOnlyList<ProveedorPlataformaCaeCandidatoDto>> ResolverPorUrlAsync(string url, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task<IReadOnlyList<ProveedorPlataformaCaeCandidatoDto>> ResolverPorDominioCorreoAsync(string email, CancellationToken cancellationToken = default) => throw NoDeberia();
    }

    private void RegistrarServiciosBasicos(IMediator mediador)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
    }

    private static EventCallback<string> SinEfecto(object suscriptor) =>
        EventCallback.Factory.Create<string>(suscriptor, _ => { });

    [Fact]
    public void VehiculoWorkspacePanel_marca_la_pestana_activa()
    {
        var id = Guid.NewGuid();
        var detalle = new VehiculoDetalleDto(id, Guid.NewGuid(), null, "Refrielectric S.A.", "Furgoneta 12", "Transit", "1234-ABC", Guid.NewGuid());
        RegistrarServiciosBasicos(new MediatorFalso(request => request switch
        {
            ObtenerVehiculoPorIdQuery q when q.Id == id => detalle,
            _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
        }));

        var cut = Render<VehiculoWorkspacePanel>(p => p
            .Add(x => x.EntidadId, id)
            .Add(x => x.PestanaActiva, "informacion")
            .Add(x => x.PestanaActivaChanged, SinEfecto(this)));

        cut.Find("[role=tab][aria-selected='true']").TextContent.Trim().Should().Be("Información");
    }

    [Fact]
    public void TrabajadorWorkspacePanel_marca_la_pestana_activa()
    {
        var id = Guid.NewGuid();
        var detalle = new TrabajadorDetalleDto(
            id, Guid.NewGuid(), null, "Refrielectric S.A.", "Marco", "Vila", "12884021K",
            new DateOnly(1990, 1, 1), null, null, null, null, null, Guid.NewGuid());
        RegistrarServiciosBasicos(new MediatorFalso(request => request switch
        {
            ObtenerTrabajadorPorIdQuery q when q.Id == id => detalle,
            _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
        }));

        var cut = Render<TrabajadorWorkspacePanel>(p => p
            .Add(x => x.EntidadId, id)
            .Add(x => x.PestanaActiva, "informacion")
            .Add(x => x.PestanaActivaChanged, SinEfecto(this)));

        cut.Find("[role=tab][aria-selected='true']").TextContent.Trim().Should().Be("Información");
    }

    [Fact]
    public void ClienteWorkspacePanel_marca_la_pestana_activa()
    {
        var id = Guid.NewGuid();
        var detalle = new ClienteDetalleDto(id, "Pinturas Lauburu S.A.", "A-48.007.615", false, null, DateTime.UtcNow, null, Guid.NewGuid());
        RegistrarServiciosBasicos(new MediatorFalso(request => request switch
        {
            ObtenerClientePorIdQuery q when q.Id == id => detalle,
            _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
        }));

        var cut = Render<ClienteWorkspacePanel>(p => p
            .Add(x => x.EntidadId, id)
            .Add(x => x.PestanaActiva, "informacion")
            .Add(x => x.PestanaActivaChanged, SinEfecto(this)));

        cut.Find("[role=tab][aria-selected='true']").TextContent.Trim().Should().Be("Información");
    }

    [Fact]
    public void EmpresaWorkspacePanel_marca_la_pestana_activa()
    {
        var id = Guid.NewGuid();
        var detalle = new EmpresaDetalleDto(id, "Montajes Ebro S.L.", "B-50.123.456", DateTime.UtcNow, [], Guid.NewGuid());
        RegistrarServiciosBasicos(new MediatorFalso(request => request switch
        {
            ObtenerEmpresaPorIdQuery q when q.Id == id => detalle,
            ObtenerCumplimientoEmpresaQuery q when q.EmpresaId == id => 80,
            _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
        }));

        var cut = Render<EmpresaWorkspacePanel>(p => p
            .Add(x => x.EntidadId, id)
            .Add(x => x.PestanaActiva, "informacion")
            .Add(x => x.PestanaActivaChanged, SinEfecto(this)));

        cut.Find("[role=tab][aria-selected='true']").TextContent.Trim().Should().Be("Información");
    }

    [Fact]
    public void CentroWorkspacePanel_marca_la_pestana_activa()
    {
        var id = Guid.NewGuid();
        var clienteId = Guid.NewGuid();
        var detalle = new CentroDetalleDto(
            id, clienteId, "Refrielectric S.A.", Guid.NewGuid(), "Montajes Ebro S.L.",
            "Centro Norte", "C-001", null, null, null, Guid.NewGuid());
        RegistrarServiciosBasicos(new MediatorFalso(request => request switch
        {
            ObtenerCentroPorIdQuery q when q.Id == id => detalle,
            ObtenerUltimaReclamacionClienteQuery q when q.ClienteId == clienteId => null,
            ObtenerLoteReclamacionQuery q when q.CentroId == id => Array.Empty<LoteReclamacionClienteDto>(),
            ObtenerEstadoCentroQuery q when q.CentroId == id => null,
            _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
        }));
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<IFileStorageService, AlmacenArchivosQueNadieDebeTocar>();
        Services.AddScoped<IResolucionProveedorPlataformaCaeService, ResolucionProveedorQueNadieDebeTocar>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        var cut = Render<CentroWorkspacePanel>(p => p
            .Add(x => x.EntidadId, id)
            .Add(x => x.PestanaActiva, "informacion")
            .Add(x => x.PestanaActivaChanged, SinEfecto(this)));

        cut.Find("[role=tab][aria-selected='true']").TextContent.Trim().Should().Be("Información");
    }
}
