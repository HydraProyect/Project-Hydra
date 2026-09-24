using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Asignaciones.Commands.CrearAsignacion;
using CaeManager.Application.Centros.Commands.CrearCentro;
using CaeManager.Application.Centros.Queries.ObtenerCentroPorId;
using CaeManager.Application.Clientes.Commands.CrearCliente;
using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Empresas.Commands.CrearEmpresa;
using CaeManager.Application.Empresas.Commands.EditarEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Trabajadores.Commands.CrearTrabajador;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Clientes.Pages;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Revisión de solo lectura del 2026-09-12 sobre <c>AltaGuiada.razor.cs</c>: el
/// alta del Trabajador (<c>GuardarTrabajadorAsync</c>) y la del Cliente
/// empresarial (<c>CrearClienteNuevoAsync</c>) encadenaban dos escrituras
/// independientes — crear la entidad y, aparte, vincularla/asignarla — y
/// anunciaban el desenlace como si fuera uno solo. Si la segunda fallaba, el
/// aviso solo hablaba del fallo y no decía que la primera SÍ se había
/// persistido: quien reintentaba creaba un duplicado o chocaba contra un DNI
/// o una razón social ya guardados.
///
/// <para>
/// Esta clase prueba los tres desenlaces (nada / parcial / completo) de esas
/// dos cadenas tras la corrección, la guarda de reentrada de
/// <c>GuardarTrabajadorAsync</c> comprobada al entrar (no solo apagada al
/// salir), y que el texto visible del asistente completo rotula al Cliente
/// empresarial «Cliente»/«Clientes» — nunca «Cliente empresarial»/«Clientes
/// empresariales» en pantalla (contrato Gen2 § 14, decisión del propietario
/// del 2026-09-23). En código, comentarios y nombres de tipo sigue el término
/// canónico «Cliente empresarial».
/// </para>
///
/// <para>
/// <b>Lo que esto NO prueba:</b> la autorización real de los Commands, ni la
/// resolución de los identificadores que llegan por query string contra su
/// alcance de tenant/cartera — <c>MediadorControlado</c> responde
/// <c>ObtenerEmpresaPorIdQuery</c>/<c>ObtenerClientePorIdQuery</c>/
/// <c>ObtenerCentroPorIdQuery</c> con lo que cada test precarga en
/// <c>Empresa</c>/<c>Cliente</c>/<c>Centro</c>, sin ningún alcance real de
/// por medio; esa propiedad la prueba <c>AltaGuiadaResolucionIdentificadoresTests</c>
/// (componente) y <c>ObtenerPorIdMultiTenantTests</c> (Postgres real).
/// </para>
/// </summary>
public class AltaGuiadaDesenlacesParcialesTests : BunitContext
{
    // ---------------------------------------------------------------- doble

    /// <summary>
    /// Mediador que responde por tipo, apunta lo enviado y permite retener una
    /// respuesta (<see cref="Interceptar"/>) para forzar una reentrada de
    /// verdad — mismo patrón que <c>ReclamacionesTabTests.MediadorControlado</c>.
    /// </summary>
    private sealed class MediadorControlado : IMediator
    {
        public List<object> Enviadas { get; } = [];

        public IReadOnlyList<EmpresaSelectorDto> CatalogoEmpresas { get; set; } = [];
        public IReadOnlyList<ClienteSelectorDto> CatalogoClientes { get; set; } = [];
        public EmpresaDetalleDto? Empresa { get; set; }
        public ClienteDetalleDto? Cliente { get; set; }
        public CentroDetalleDto? Centro { get; set; }

        public Func<CrearEmpresaCommand, Result<Guid>> AlCrearEmpresa { get; set; } = _ => Result.Exito(Guid.NewGuid());
        public Func<CrearClienteCommand, Result<Guid>> AlCrearCliente { get; set; } = _ => Result.Exito(Guid.NewGuid());
        public Func<EditarEmpresaCommand, Result> AlEditarEmpresa { get; set; } = _ => Result.Exito();
        public Func<CrearCentroCommand, Result<Guid>> AlCrearCentro { get; set; } = _ => Result.Exito(Guid.NewGuid());
        public Func<CrearTrabajadorCommand, Result<Guid>> AlCrearTrabajador { get; set; } = _ => Result.Exito(Guid.NewGuid());
        public Func<CrearAsignacionCommand, Result<Guid>> AlCrearAsignacion { get; set; } = _ => Result.Exito(Guid.NewGuid());

        /// <summary>Si devuelve una tarea, ese Command se queda esperando a que el test la suelte.</summary>
        public Func<object, Task<object?>?>? Interceptar { get; set; }

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);

            if (Interceptar?.Invoke(request) is { } retenida)
                return (TResponse)(await retenida)!;

            return (TResponse)Responder(request)!;
        }

        private object? Responder(object request) => request switch
        {
            ObtenerEmpresasParaSelectorQuery => CatalogoEmpresas,
            ObtenerClientesParaSelectorQuery => CatalogoClientes,
            ObtenerEmpresaPorIdQuery => Empresa,
            ObtenerClientePorIdQuery => Cliente,
            ObtenerCentroPorIdQuery => Centro,
            CrearEmpresaCommand c => AlCrearEmpresa(c),
            CrearClienteCommand c => AlCrearCliente(c),
            EditarEmpresaCommand c => AlEditarEmpresa(c),
            CrearCentroCommand c => AlCrearCentro(c),
            CrearTrabajadorCommand c => AlCrearTrabajador(c),
            CrearAsignacionCommand c => AlCrearAsignacion(c),
            _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
        };

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

    // ---------------------------------------------------------------- arnés

    private (IRenderedComponent<AltaGuiada> Cut, MediadorControlado Mediador, ToastService Toasts) Renderizar(
        string ruta, MediadorControlado? mediador = null)
    {
        mediador ??= new MediadorControlado();

        Services.AddLocalization();
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<IValidator<CrearClienteCommand>>(_ => new InlineValidator<CrearClienteCommand>());
        Services.AddScoped<IValidator<CrearEmpresaCommand>>(_ => new InlineValidator<CrearEmpresaCommand>());
        Services.AddScoped<IValidator<CrearCentroCommand>>(_ => new InlineValidator<CrearCentroCommand>());
        Services.AddScoped<IValidator<CrearTrabajadorCommand>>(_ => new InlineValidator<CrearTrabajadorCommand>());

        Services.GetRequiredService<NavigationManager>().NavigateTo(ruta);

        var cut = Render<AltaGuiada>();
        var toasts = Services.GetRequiredService<ToastService>();
        return (cut, mediador, toasts);
    }

    private static IElement Boton(IRenderedComponent<AltaGuiada> cut, string texto) =>
        cut.FindAll("button").Single(b => b.TextContent.Contains(texto));

    /// <summary>
    /// Escribe en el CampoTexto de índice <paramref name="indice"/> entre los
    /// <c>input[type=text]</c> visibles y fuerza el blur — CampoTexto rebota
    /// 300 ms y solo notifica en oninput/onblur, nunca en onchange; el blur
    /// vuelca el valor ya mismo, sin esperar al temporizador. Se vuelve a
    /// buscar el input cada vez (no se reutiliza una referencia de un
    /// <c>FindAll</c> anterior) por si un render de por medio lo sustituye.
    /// </summary>
    private static async Task RellenarCampoTextoAsync(IRenderedComponent<AltaGuiada> cut, int indice, string valor)
    {
        var campo = cut.FindAll("input[type=text]")[indice];
        await campo.InputAsync(new ChangeEventArgs { Value = valor });
        await cut.FindAll("input[type=text]")[indice].BlurAsync(new FocusEventArgs());
    }

    // ------------------------------------------------- desenlace: Cliente empresarial

    /// <summary>
    /// CrearClienteCommand triunfa y EditarEmpresaCommand (la vinculación con
    /// la Empresa) falla: el Cliente empresarial YA existe. El aviso tiene que
    /// decirlo, y un reintento tiene que resolver solo la vinculación —
    /// <b>nunca volver a mandar CrearClienteCommand</b>, que chocaría contra el
    /// registro ya persistido.
    /// </summary>
    [Fact]
    public async Task Si_falla_la_vinculacion_el_aviso_dice_que_el_Cliente_empresarial_ya_se_creo_y_el_reintento_no_lo_repite()
    {
        var empresaId = Guid.NewGuid();
        var mediador = new MediadorControlado
        {
            Empresa = new EmpresaDetalleDto(empresaId, "Montajes Ebro S.L.", null, DateTime.UtcNow, [], Guid.NewGuid()),
            AlEditarEmpresa = _ => Result.Fallo(Error.Crear("Empresa.Concurrencia", "La empresa cambió mientras tanto.")),
        };

        var (cut, _, toasts) = Renderizar($"clientes/alta-guiada?empresaId={empresaId}", mediador);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("2. Cliente</h2>"));

        // CampoTexto rebota 300 ms y notifica en oninput/onblur, no en onchange:
        // el blur vuelca el valor ya mismo, sin esperar al temporizador. Se
        // vuelve a buscar el input tras cada blur por si el render lo sustituye.
        await RellenarCampoTextoAsync(cut, 0, "Refrielectric SL");
        await RellenarCampoTextoAsync(cut, 1, "B00000000");

        await Boton(cut, "Guardar y continuar a Centro").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<CrearClienteCommand>().Should().ContainSingle("el Cliente empresarial se crea una sola vez");
        mediador.Enviadas.OfType<EditarEmpresaCommand>().Should().ContainSingle("primer intento de vinculación, fallido");

        toasts.Mensajes.Should().ContainSingle(m =>
            m.Tono == TonoToast.Advertencia
            && m.Mensaje.StartsWith("Cliente «Refrielectric SL» creado")
            && m.Mensaje.Contains("La empresa cambió mientras tanto."),
            "el aviso debe decir que el Cliente empresarial YA se creó, no solo que la vinculación falló");

        cut.Markup.Should().Contain("Vinculación con la Empresa pendiente",
            "el paso se queda en un resumen honesto, no vuelve a ofrecer el formulario de alta");
        cut.Markup.Should().NotContain("Razón social",
            "con el Cliente empresarial ya creado, repintar el formulario invitaría a repetir el alta");

        // Reintento: ahora la vinculación sí funciona.
        mediador.AlEditarEmpresa = _ => Result.Exito();
        await Boton(cut, "Reintentar vinculación").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<CrearClienteCommand>().Should().ContainSingle(
            "el reintento resuelve solo la vinculación pendiente — nunca vuelve a crear el Cliente empresarial");
        mediador.Enviadas.OfType<EditarEmpresaCommand>().Should().HaveCount(2, "primer intento fallido más el reintento");

        cut.Markup.Should().Contain("Vinculado a la Empresa", "el resumen refleja el nuevo estado tras el reintento");
        toasts.Mensajes.Should().Contain(m => m.Tono == TonoToast.Exito && m.Mensaje == "Empresa vinculada al Cliente.");
        SinClienteEmpresarialVisible(cut.Markup, toasts);
    }

    // ------------------------------------------------- desenlace: Trabajador

    /// <summary>
    /// CrearTrabajadorCommand triunfa y CrearAsignacionCommand falla: el
    /// Trabajador YA existe. El resumen no puede decir "asignado al centro"
    /// (no ocurrió), tiene que ofrecer reintentar la asignación sola, y el
    /// formulario se vacía para que un segundo trabajador no choque con el
    /// DNI del que ya se creó.
    /// </summary>
    [Fact]
    public async Task Si_falla_la_asignacion_el_resumen_no_dice_asignado_y_el_reintento_no_repite_el_alta()
    {
        var empresaId = Guid.NewGuid();
        var clienteId = Guid.NewGuid();
        var centroId = Guid.NewGuid();
        var mediador = new MediadorControlado
        {
            Empresa = new EmpresaDetalleDto(empresaId, "Montajes Ebro S.L.", null, DateTime.UtcNow, [], Guid.NewGuid()),
            Cliente = new ClienteDetalleDto(clienteId, "Refrielectric", "B00000000", false, null, DateTime.UtcNow, null, Guid.NewGuid()),
            Centro = new CentroDetalleDto(
                centroId, clienteId, "Refrielectric", empresaId, "Montajes Ebro S.L.", "Nave 1", null, null, null, null, Guid.NewGuid()),
            AlCrearAsignacion = _ => Result.Fallo<Guid>(Error.Crear("Asignacion.Duplicada", "Ya existe una asignación activa en este centro.")),
        };

        var (cut, _, toasts) = Renderizar(
            $"clientes/alta-guiada?empresaId={empresaId}&clienteId={clienteId}&centroId={centroId}",
            mediador);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("4. Trabajadores"));

        await RellenarCampoTextoAsync(cut, 0, "Marta"); // Nombre
        await RellenarCampoTextoAsync(cut, 1, "Ruiz"); // Apellidos
        await RellenarCampoTextoAsync(cut, 2, "12345678Z"); // DNI

        await Boton(cut, "Guardar trabajador").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<CrearTrabajadorCommand>().Should().ContainSingle("el Trabajador se crea una sola vez");
        mediador.Enviadas.OfType<CrearAsignacionCommand>().Should().ContainSingle("primer intento de asignación, fallido");

        toasts.Mensajes.Should().ContainSingle(m =>
            m.Tono == TonoToast.Advertencia
            && m.Mensaje.Contains("Marta Ruiz")
            && m.Mensaje.Contains("se creó")
            && m.Mensaje.Contains("Ya existe una asignación activa en este centro."),
            "el aviso debe decir que Marta Ruiz YA se creó, no solo que la asignación falló");

        cut.Markup.Should().Contain("Marta Ruiz</strong> creado.",
            "sin ' y asignado al centro': esa segunda escritura no ocurrió");
        // <strong> lleva el atributo de aislamiento de CSS (scoped css) del
        // propio componente: no se puede comparar el literal exacto de la
        // etiqueta de apertura.
        Regex.IsMatch(cut.Markup, @"La asignación de <strong[^>]*>Marta Ruiz</strong> a Nave 1 no se completó").Should().BeTrue();

        // El formulario ya está vacío: un segundo guardado no puede chocar con este DNI.
        cut.Find("input[type=text]").GetAttribute("value").Should().BeNullOrEmpty();

        // Reintento: ahora la asignación sí funciona.
        mediador.AlCrearAsignacion = _ => Result.Exito(Guid.NewGuid());
        await Boton(cut, "Reintentar asignación").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<CrearTrabajadorCommand>().Should().ContainSingle(
            "el reintento resuelve solo la asignación pendiente — nunca vuelve a crear el Trabajador");
        mediador.Enviadas.OfType<CrearAsignacionCommand>().Should().HaveCount(2, "primer intento fallido más el reintento");

        cut.Markup.Should().Contain("Marta Ruiz</strong> creado y asignado al centro");
        cut.Markup.Should().NotContain("no se completó", "el reintento resolvió la asignación pendiente");
    }

    // ------------------------------------------------- contrato: reentrada

    /// <summary>
    /// Dos clics en "Guardar trabajador" mandan UN CrearTrabajadorCommand. El
    /// botón deshabilitado (Cargando) no basta — Boton conserva su @onclick
    /// enganchado — así que la guarda tiene que comprobarse al ENTRAR al
    /// método, no solo apagarse al salir (mismo contrato que
    /// ReclamacionesTabTests, aplicado aquí a GuardarTrabajadorAsync).
    /// </summary>
    [Fact]
    public async Task Dos_clics_en_Guardar_trabajador_mandan_un_solo_CrearTrabajadorCommand()
    {
        var empresaId = Guid.NewGuid();
        var clienteId = Guid.NewGuid();
        var centroId = Guid.NewGuid();
        var mediador = new MediadorControlado
        {
            Empresa = new EmpresaDetalleDto(empresaId, "Montajes Ebro S.L.", null, DateTime.UtcNow, [], Guid.NewGuid()),
            Cliente = new ClienteDetalleDto(clienteId, "Refrielectric", "B00000000", false, null, DateTime.UtcNow, null, Guid.NewGuid()),
            Centro = new CentroDetalleDto(
                centroId, clienteId, "Refrielectric", empresaId, "Montajes Ebro S.L.", "Nave 1", null, null, null, null, Guid.NewGuid()),
        };

        var retenido = new TaskCompletionSource<object?>();
        mediador.Interceptar = p => p is CrearTrabajadorCommand ? retenido.Task : null;

        var (cut, _, _) = Renderizar(
            $"clientes/alta-guiada?empresaId={empresaId}&clienteId={clienteId}&centroId={centroId}",
            mediador);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("4. Trabajadores"));

        await RellenarCampoTextoAsync(cut, 0, "Marta");
        await RellenarCampoTextoAsync(cut, 1, "Ruiz");
        await RellenarCampoTextoAsync(cut, 2, "12345678Z");

        // Igual que en ReclamacionesTabTests: no esperar cada clic por
        // separado — el segundo tiene que llegar con el primero todavía en
        // vuelo para probar la guarda de verdad, no simularla.
        var primero = Boton(cut, "Guardar trabajador").ClickAsync(new MouseEventArgs());
        var segundo = Boton(cut, "Guardar trabajador").ClickAsync(new MouseEventArgs());

        await cut.InvokeAsync(() => retenido.SetResult(Result.Exito(Guid.NewGuid())));
        await primero;
        await segundo;

        mediador.Enviadas.OfType<CrearTrabajadorCommand>().Should().ContainSingle(
            "el segundo clic llegó con el primero todavía en vuelo");
    }

    // ------------------------------------------------- terminología visible

    /// <summary>
    /// Contrato Gen2 § 14 (decisión del propietario, 2026-09-23): en texto
    /// visible el Cliente empresarial se rotula «Cliente»/«Clientes». Recorre
    /// el asistente completo (Empresa → Cliente → Centro → Trabajadores) con
    /// las cuatro escrituras en éxito —pasando por el catálogo vacío de la
    /// casilla «Vincular a un Cliente que ya existe»— y comprueba en cada paso
    /// los rótulos nuevos y que ni el marcado ni los toasts dicen «Cliente
    /// empresarial»/«Clientes empresariales». Devolver cualquiera de esos
    /// textos a la forma larga debe tumbar este test.
    /// </summary>
    [Fact]
    public async Task El_asistente_completo_dice_Cliente_y_nunca_Cliente_empresarial()
    {
        // El Id de Empresa es fijo (no el que generaría por defecto
        // AlCrearEmpresa) para poder precargar Empresa con el mismo Id: la
        // vinculación Cliente→Empresa (VincularEmpresaAClienteAsync) consulta
        // ObtenerEmpresaPorIdQuery contra ese Id y necesita encontrarla para
        // triunfar en el primer intento — este test recorre el camino
        // feliz de principio a fin, no el parcial (ver los dos tests de arriba).
        var empresaId = Guid.NewGuid();
        var mediador = new MediadorControlado
        {
            AlCrearEmpresa = _ => Result.Exito(empresaId),
            Empresa = new EmpresaDetalleDto(empresaId, "Montajes Ebro S.L.", null, DateTime.UtcNow, [], Guid.NewGuid()),
        };
        var (cut, _, toasts) = Renderizar("clientes/alta-guiada", mediador);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("1. Empresa"));
        cut.Find("h1").TextContent.Trim().Should().Be("Alta guiada de Cliente");
        cut.Markup.Should().Contain("Da de alta la Empresa, su Cliente, el primer Centro");
        cut.Find("a.enlace-exportar").TextContent.Trim().Should().Be("Volver a Clientes");
        cut.Markup.Should().MatchRegex(@">\s*Clientes\s*<", "la primera miga rotula la lista «Clientes»");
        cut.Markup.Should().MatchRegex(@">\s*Cliente\s*<", "el indicador de pasos rotula el paso 2 «Cliente»");
        SinClienteEmpresarialVisible(cut.Markup, toasts);

        await RellenarCampoTextoAsync(cut, 0, "Montajes Ebro S.L.");
        await Boton(cut, "Guardar y continuar").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("2. Cliente</h2>"));
        cut.Markup.Should().Contain("Vincular a un Cliente que ya existe");
        cut.Markup.Should().Contain("Cliente crítico");
        cut.Markup.Should().Contain("Notas del Cliente");
        SinClienteEmpresarialVisible(cut.Markup, toasts);

        // La casilla con el catálogo vacío: estado vacío propio, sin «Cliente
        // empresarial». Se vuelve a desmarcar para seguir por el alta nueva.
        await cut.Find("input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = true });
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Todavía no hay ningún Cliente dado de alta"));
        cut.Markup.Should().Contain("crea el primer Cliente aquí mismo.");
        SinClienteEmpresarialVisible(cut.Markup, toasts);
        await cut.Find("input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = false });

        await RellenarCampoTextoAsync(cut, 0, "Refrielectric SL");
        await RellenarCampoTextoAsync(cut, 1, "B00000000");
        await Boton(cut, "Guardar y continuar a Centro").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("3. Centro"));
        toasts.Mensajes.Should().Contain(m => m.Tono == TonoToast.Exito && m.Mensaje == "Cliente creado correctamente.");
        // <strong> lleva el atributo de aislamiento de CSS del componente: no
        // se puede comparar el literal exacto de la etiqueta de apertura.
        Regex.IsMatch(cut.Markup, @"· Cliente: <strong[^>]*>Refrielectric SL").Should().BeTrue();
        SinClienteEmpresarialVisible(cut.Markup, toasts);

        await RellenarCampoTextoAsync(cut, 0, "Nave 1");
        await Boton(cut, "Guardar centro").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => Boton(cut, "Ver el Cliente").TextContent.Trim().Should().Be("Ver el Cliente"));
        cut.Markup.Should().Contain("para esta misma Empresa y este mismo Cliente sin salir del paso.");
        SinClienteEmpresarialVisible(cut.Markup, toasts);

        await Boton(cut, "Continuar a Trabajadores").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("4. Trabajadores"));
        SinClienteEmpresarialVisible(cut.Markup, toasts);
    }

    /// <summary>
    /// La rama «Vincular a un Cliente que ya existe» con catálogo: etiqueta y
    /// opción vacía del selector, error de validación sin selección y toast
    /// de éxito dicen «Cliente», nunca «Cliente empresarial» (§ 14).
    /// </summary>
    [Fact]
    public async Task Vincular_a_un_Cliente_existente_dice_Cliente_en_selector_error_y_toast()
    {
        var empresaId = Guid.NewGuid();
        var clienteId = Guid.NewGuid();
        var mediador = new MediadorControlado
        {
            Empresa = new EmpresaDetalleDto(empresaId, "Montajes Ebro S.L.", null, DateTime.UtcNow, [], Guid.NewGuid()),
            CatalogoClientes = [new ClienteSelectorDto(clienteId, "Refrielectric SL")],
        };
        var (cut, _, toasts) = Renderizar($"clientes/alta-guiada?empresaId={empresaId}", mediador);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("2. Cliente</h2>"));
        await cut.Find("input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = true });

        cut.WaitForAssertion(() => cut.Find("select").Should().NotBeNull());
        var idSelect = cut.Find("select").Id;
        cut.Find($"label[for='{idSelect}']").TextContent.Trim().Should().Be("Cliente");
        cut.Find("select option[value='']").TextContent.Trim().Should().Be("Selecciona un Cliente…");

        await Boton(cut, "Vincular y continuar a Centro").ClickAsync(new MouseEventArgs());
        cut.Find("[role=alert]").TextContent.Trim().Should().Be("Selecciona un Cliente.");
        SinClienteEmpresarialVisible(cut.Markup, toasts);

        await cut.Find("select").ChangeAsync(new ChangeEventArgs { Value = clienteId.ToString() });
        await Boton(cut, "Vincular y continuar a Centro").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("3. Centro"));
        toasts.Mensajes.Should().Contain(m => m.Tono == TonoToast.Exito && m.Mensaje == "Empresa vinculada al Cliente.");
        SinClienteEmpresarialVisible(cut.Markup, toasts);
    }

    private static readonly Regex ClienteEmpresarial =
        new(@"\bClientes?\s+empresarial(?:es)?\b", RegexOptions.IgnoreCase);

    private static void SinClienteEmpresarialVisible(string markup, ToastService toasts)
    {
        ClienteEmpresarial.IsMatch(markup).Should().BeFalse(
            "contrato Gen2 § 14: en pantalla el Cliente empresarial se rotula «Cliente»/«Clientes», nunca «Cliente empresarial»");
        toasts.Mensajes.Should().NotContain(m => ClienteEmpresarial.IsMatch(m.Mensaje),
            "contrato Gen2 § 14: tampoco los toasts dicen «Cliente empresarial»");
    }
}
