using System.Net;
using AngleSharp.Dom;
using Bunit;
using Bunit.TestDoubles;
using CaeManager.Application.Common;
using CaeManager.Application.Cumplimiento.AceptacionContractual;
using CaeManager.Domain.Common;
using CaeManager.Web.Features.Cumplimiento;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// #2009, D-6: el gate de aceptación contractual fuera del alta. Con el catálogo vacío (la consulta devuelve una
/// lista vacía, como hoy en producción) no pinta nada. Con documentos pendientes bloquea con una casilla por
/// documento —condiciones generales y contrato de encargo separadas—, ninguna premarcada, la declaración de
/// facultades aparte y el botón deshabilitado hasta marcarlo todo.
/// </summary>
public class AceptacionContractualGateTests : BunitContext
{
    private static readonly DocumentoContractualPendienteDto Condiciones =
        new("Condiciones", "Condiciones generales de prueba", "1.0", new string('a', 64));
    private static readonly DocumentoContractualPendienteDto Encargo =
        new("Encargo", "Contrato de encargo de prueba", "2.1", new string('b', 64));

    public AceptacionContractualGateTests()
    {
        this.AddBunitPersistentComponentState();
        Services.AddLocalization();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<ICurrentUserService>(new UsuarioFalso());
        Services.AddSingleton<IHttpContextAccessor>(_accesorHttp);
    }

    /// <summary>Sin contexto salvo que el test lo ponga: así el accesor no tapa la falta del HttpContext en cascada.</summary>
    private readonly HttpContextAccessor _accesorHttp = new();

    private sealed class MediatorFalso(params DocumentoContractualPendienteDto[] pendientes) : IMediator
    {
        public List<AceptarDocumentosContractualesCommand> Aceptaciones { get; } = [];
        public int Consultas { get; private set; }
        public Result ResultadoAceptar { get; set; } = Result.Exito();
        /// <summary>Lo que devuelve la consulta después de un intento fallido; por defecto, lo mismo que antes.</summary>
        public IReadOnlyList<DocumentoContractualPendienteDto>? PendientesTrasFallo { get; set; }
        /// <summary>Cuántas de las primeras consultas lanzan, como una base caída.</summary>
        public int ConsultasQueFallan { get; set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            switch (request)
            {
                case ObtenerAceptacionesContractualesPendientesQuery:
                    Consultas++;
                    if (Consultas <= ConsultasQueFallan)
                        throw new InvalidOperationException("Fallo simulado de la consulta.");
                    IReadOnlyList<DocumentoContractualPendienteDto> lista = Aceptaciones.Count == 0 ? pendientes
                        : ResultadoAceptar.EsExitoso ? [] : PendientesTrasFallo ?? pendientes;
                    return Task.FromResult((TResponse)(object)lista);
                case AceptarDocumentosContractualesCommand comando:
                    Aceptaciones.Add(comando);
                    return Task.FromResult((TResponse)(object)ResultadoAceptar);
                default:
                    throw new NotSupportedException(request.GetType().Name);
            }
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class UsuarioFalso : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.Parse("eeeeeeee-0000-0000-0000-000000000001"));
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.Parse("eeeeeeee-0000-0000-0000-0000000000aa"));
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private IRenderedComponent<AceptacionContractualGate> RenderGate(MediatorFalso mediador, bool conContextoHttp = true)
    {
        Services.AddSingleton<IMediator>(mediador);
        if (!conContextoHttp)
            return Render<AceptacionContractualGate>();

        var contexto = new DefaultHttpContext();
        contexto.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:203.0.113.20");
        return Render<AceptacionContractualGate>(p => p.AddCascadingValue(contexto));
    }

    private static void MarcarTodo(IRenderedComponent<AceptacionContractualGate> gate)
    {
        Casilla(gate, "Condiciones").Change(true);
        Casilla(gate, "Encargo").Change(true);
        gate.Find(".campo-facultades input").Change(true);
    }

    [Fact]
    public void Sin_IP_del_prerenderizado_recarga_la_pagina_en_vez_de_quedar_bloqueado()
    {
        var mediador = new MediatorFalso(Condiciones, Encargo);
        var gate = RenderGate(mediador, conContextoHttp: false);

        MarcarTodo(gate);
        Boton(gate).Click();

        mediador.Aceptaciones.Should().BeEmpty("sin IP el Command siempre fallaría");
        var navegacion = Services.GetRequiredService<BunitNavigationManager>();
        navegacion.History.Should().ContainSingle().Which.Options.ForceLoad.Should().BeTrue(
            "la recarga vuelve a pasar por el prerenderizado, que lee la IP");
    }

    [Fact]
    public void Sin_HttpContext_en_cascada_toma_la_IP_del_accesor_y_acepta_sin_recargar()
    {
        var contexto = new DefaultHttpContext();
        contexto.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.30");
        _accesorHttp.HttpContext = contexto;
        var mediador = new MediatorFalso(Condiciones, Encargo);
        var gate = RenderGate(mediador, conContextoHttp: false);

        MarcarTodo(gate);
        Boton(gate).Click();

        mediador.Aceptaciones.Should().ContainSingle().Which.Ip.Should().Be("203.0.113.30",
            "un gate InteractiveServer en el layout no siempre recibe el HttpContext en cascada");
        Services.GetRequiredService<BunitNavigationManager>().History.Should().BeEmpty("con IP no hace falta recargar");
        gate.FindAll("[role=dialog]").Should().BeEmpty();
    }

    [Fact]
    public void Si_lo_pendiente_cambio_con_el_modal_abierto_lo_vuelve_a_leer()
    {
        var condicionesNuevas = Condiciones with { Version = "1.1", HashSha256 = new string('c', 64) };
        var mediador = new MediatorFalso(Condiciones, Encargo)
        {
            ResultadoAceptar = Result.Fallo(Error.Crear("AceptacionContractual.VersionNoPublicada", "Versión no vigente.")),
            PendientesTrasFallo = [condicionesNuevas, Encargo],
        };
        var gate = RenderGate(mediador);

        MarcarTodo(gate);
        Boton(gate).Click();

        gate.Find(".alerta-formulario").TextContent.Should().Be("Versión no vigente.");
        gate.FindAll(".documento-contractual-pendiente a").Select(a => a.GetAttribute("href")).Should().Equal(
            "/legal/contrato/Condiciones/1.1", "/legal/contrato/Encargo/2.1");
        gate.FindAll("input[type=checkbox]").Should().OnlyContain(c => !c.HasAttribute("checked"),
            "la lista nueva se vuelve a aceptar entera");
        Boton(gate).HasAttribute("disabled").Should().BeTrue();
    }

    private static IElement Boton(IRenderedComponent<AceptacionContractualGate> gate) =>
        gate.FindAll("button").Single(b => b.TextContent.Trim() == "Aceptar y continuar");

    private static IElement Casilla(IRenderedComponent<AceptacionContractualGate> gate, string documento) =>
        gate.Find($".documento-contractual-pendiente[data-documento='{documento}'] input[type=checkbox]");

    [Fact]
    public void Con_el_catalogo_vacio_no_bloquea_a_nadie()
    {
        var mediador = new MediatorFalso();

        var gate = RenderGate(mediador);

        mediador.Consultas.Should().Be(1);
        gate.FindAll("[role=dialog]").Should().BeEmpty("sin documentos publicados no hay nada que aceptar");
        gate.FindAll("input[type=checkbox]").Should().BeEmpty();
    }

    private static IElement BotonReintentar(IRenderedComponent<AceptacionContractualGate> gate) =>
        gate.FindAll("button").Single(b => b.TextContent.Trim() == "Reintentar");

    [Fact]
    public void Si_la_consulta_falla_bloquea_con_un_error_y_no_lo_toma_por_sin_pendientes()
    {
        var mediador = new MediatorFalso(Condiciones, Encargo) { ConsultasQueFallan = 1 };

        var gate = RenderGate(mediador);

        gate.FindAll("[role=dialog]").Should().ContainSingle("sin la consulta no se sabe si falta aceptar");
        gate.Find(".gate-contractual-error").TextContent.Should().Be(
            "Algo ha fallado al comprobar tu cuenta. Vuelve a intentarlo en unos segundos.");
        gate.FindAll("button").Select(b => b.TextContent.Trim()).Should().Equal(["Reintentar"],
            "es bloqueante: no hay forma de cerrarlo sin volver a consultar");
        gate.FindAll(".documento-contractual-pendiente").Should().BeEmpty();
    }

    [Fact]
    public void El_reintento_vuelve_a_consultar_y_abre_lo_pendiente()
    {
        var mediador = new MediatorFalso(Condiciones, Encargo) { ConsultasQueFallan = 1 };
        var gate = RenderGate(mediador);

        BotonReintentar(gate).Click();

        mediador.Consultas.Should().Be(2);
        gate.FindAll(".gate-contractual-error").Should().BeEmpty();
        gate.FindAll(".documento-contractual-pendiente").Should().HaveCount(2);
    }

    [Fact]
    public void El_reintento_sin_pendientes_cierra_el_bloqueo()
    {
        var mediador = new MediatorFalso { ConsultasQueFallan = 1 };
        var gate = RenderGate(mediador);

        BotonReintentar(gate).Click();

        mediador.Consultas.Should().Be(2);
        gate.FindAll("[role=dialog]").Should().BeEmpty();
    }

    [Fact]
    public void Un_reintento_que_vuelve_a_fallar_sigue_bloqueando()
    {
        var mediador = new MediatorFalso { ConsultasQueFallan = 2 };
        var gate = RenderGate(mediador);

        BotonReintentar(gate).Click();

        mediador.Consultas.Should().Be(2);
        gate.FindAll(".gate-contractual-error").Should().ContainSingle();
    }

    [Fact]
    public void Pinta_una_casilla_por_documento_sin_premarcar_y_el_boton_deshabilitado()
    {
        var gate = RenderGate(new MediatorFalso(Condiciones, Encargo));

        gate.Find("h2").TextContent.Should().Be("Documentos contractuales pendientes");
        var documentos = gate.FindAll(".documento-contractual-pendiente");
        documentos.Select(d => d.QuerySelector("label")!.TextContent.Trim()).Should().Equal(
            "He leído y acepto Condiciones generales de prueba (versión 1.0).",
            "He leído y acepto Contrato de encargo de prueba (versión 2.1).");
        documentos.Select(d => d.QuerySelector("a")!.GetAttribute("href")).Should().Equal(
            "/legal/contrato/Condiciones/1.0", "/legal/contrato/Encargo/2.1");
        gate.Find(".campo-facultades").TextContent.Trim().Should().Be(
            "Declaro que tengo facultades para aceptar estos documentos en nombre de mi organización.");

        gate.FindAll("input[type=checkbox]").Should().HaveCount(3).And.OnlyContain(c => !c.HasAttribute("checked"),
            "ninguna casilla puede venir marcada");
        Boton(gate).HasAttribute("disabled").Should().BeTrue();
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void El_boton_sigue_deshabilitado_mientras_falte_una_casilla(bool condiciones, bool encargo, bool facultades)
    {
        var gate = RenderGate(new MediatorFalso(Condiciones, Encargo));

        if (condiciones) Casilla(gate, "Condiciones").Change(true);
        if (encargo) Casilla(gate, "Encargo").Change(true);
        if (facultades) gate.Find(".campo-facultades input").Change(true);

        Boton(gate).HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Con_todo_marcado_acepta_los_documentos_mostrados_con_la_IP_y_se_cierra()
    {
        var mediador = new MediatorFalso(Condiciones, Encargo);
        var gate = RenderGate(mediador);

        Casilla(gate, "Condiciones").Change(true);
        Casilla(gate, "Encargo").Change(true);
        gate.Find(".campo-facultades input").Change(true);
        Boton(gate).HasAttribute("disabled").Should().BeFalse();
        Boton(gate).Click();

        var comando = mediador.Aceptaciones.Should().ContainSingle().Subject;
        comando.DeclaraFacultades.Should().BeTrue();
        comando.Ip.Should().Be("203.0.113.20", "la IP del contexto HTTP, sin el prefijo IPv4 en IPv6");
        comando.Documentos.Should().Equal(
            new DocumentoContractualAceptado("Condiciones", "1.0", Condiciones.HashSha256),
            new DocumentoContractualAceptado("Encargo", "2.1", Encargo.HashSha256));
        gate.FindAll("[role=dialog]").Should().BeEmpty();
    }

    [Fact]
    public void Si_aceptar_falla_sigue_bloqueado_y_muestra_el_error()
    {
        var mediador = new MediatorFalso(Condiciones, Encargo)
        {
            ResultadoAceptar = Result.Fallo(Error.Crear("AceptacionContractual.Prueba", "Mensaje de error de prueba.")),
        };
        var gate = RenderGate(mediador);

        Casilla(gate, "Condiciones").Change(true);
        Casilla(gate, "Encargo").Change(true);
        gate.Find(".campo-facultades input").Change(true);
        Boton(gate).Click();

        gate.Find(".alerta-formulario").TextContent.Should().Be("Mensaje de error de prueba.");
        gate.FindAll("[role=dialog]").Should().ContainSingle();
    }

    [Fact]
    public void El_modal_no_se_cierra_sin_aceptar()
    {
        var gate = RenderGate(new MediatorFalso(Condiciones, Encargo));

        gate.FindAll(".modal-cerrar").Should().BeEmpty();
        gate.Find(".modal-superposicion").Click();
        gate.Find("[role=dialog]").KeyDown("Escape");

        gate.FindAll("[role=dialog]").Should().ContainSingle();
    }
}
