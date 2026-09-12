using System.Reflection;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Clientes.Commands.EditarCliente;
using CaeManager.Application.Clientes.Commands.EliminarCliente;
using CaeManager.Application.Clientes.Queries.ObtenerCentrosDeCliente;
using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Application.Clientes.Queries.ObtenerEmpresasDeCliente;
using CaeManager.Application.Clientes.Queries.ObtenerResumenCliente;
using CaeManager.Application.Clientes.Queries.ObtenerSubcontratasDeCliente;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Clientes.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Cliente 360 (<see cref="ClienteWorkspacePanel"/>) contra su mockup Gen 2
/// («Cliente 360 TALVEG.dc.html»). Prueban efectos: qué se ve, qué consultas y
/// comandos salen y con qué parámetros, qué le pasa a la pila del Context
/// Workspace. bUnit no evalúa CSS, así que ninguno afirma un estilo.
///
/// <para>
/// Las esperas se retienen con <see cref="TaskCompletionSource"/> SIN
/// continuaciones asíncronas: al liberar dentro de <c>InvokeAsync</c>, la
/// continuación del panel corre en línea en el dispatcher del renderer, y al
/// volver del <c>await</c> ya ha escrito (o descartado) lo que iba a escribir.
/// Sin eso, una comprobación negativa podría pasar solo por llegar antes que
/// la respuesta.
/// </para>
///
/// <para>
/// Las pestañas Blindaje 42.1, Documentación, Agenda y Actividad delegan en
/// componentes compartidos con los otros seis paneles y no se ejercitan aquí:
/// lo que este panel decide de ellas —que estén, en ese orden, y sin recuento
/// inventado— sí se comprueba desde la tira de pestañas.
/// </para>
/// </summary>
public class Cliente360Gen2Tests : BunitContext
{
    /// <summary>BotonCopiar importa ./js/clipboard.js.</summary>
    public Cliente360Gen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    /// <summary>
    /// Responde según los parámetros de cada consulta —el Id del Cliente
    /// empresarial—, no por tipo: una consulta con el Id equivocado recibe lo
    /// que le corresponde a ese Id, y el test lo ve.
    /// </summary>
    private sealed class MediatorFalso : IMediator
    {
        public Dictionary<Guid, ClienteDetalleDto> Detalles { get; } = [];
        public Dictionary<Guid, ResumenClienteDto> Resumenes { get; } = [];
        public Dictionary<Guid, List<EmpresaDeClienteDto>> Empresas { get; } = [];
        public Dictionary<Guid, List<SubcontrataDeClienteDto>> Subcontratas { get; } = [];
        public Dictionary<Guid, List<CentroDeClienteDto>> Centros { get; } = [];

        public Result ResultadoEliminar { get; set; } = Result.Exito();
        public Result ResultadoEditar { get; set; } = Result.Exito();

        /// <summary>Si devuelve una tarea, la respuesta espera a que se complete.</summary>
        public Func<object, Task?>? Retener { get; set; }

        /// <summary>Si devuelve true, la petición lanza en vez de responder.</summary>
        public Func<object, bool>? Lanzar { get; set; }

        public List<object> Enviadas { get; } = [];

        /// <summary>El token con el que llegó cada petición, en el mismo orden que <see cref="Enviadas"/>.</summary>
        public List<(object Peticion, CancellationToken Token)> Tokens { get; } = [];

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            Tokens.Add((request, cancellationToken));
            if (Retener?.Invoke(request) is { } retenida)
                await retenida;
            if (Lanzar?.Invoke(request) == true)
                throw new InvalidOperationException($"Fallo simulado para {request.GetType().Name}.");
            return (TResponse)Responder(request)!;
        }

        private object? Responder(object request) => request switch
        {
            ObtenerClientePorIdQuery q => Detalles.GetValueOrDefault(q.Id),
            ObtenerResumenClienteQuery q => Resumenes.GetValueOrDefault(q.ClienteId),
            ObtenerEmpresasDeClienteQuery q => Empresas.GetValueOrDefault(q.ClienteId) ?? [],
            ObtenerSubcontratasDeClienteQuery q => Subcontratas.GetValueOrDefault(q.ClienteId) ?? [],
            ObtenerCentrosDeClienteQuery q => Centros.GetValueOrDefault(q.ClienteId) ?? [],
            EditarClienteCommand => ResultadoEditar,
            EliminarClienteCommand => ResultadoEliminar,
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

    private MediatorFalso Registrar(MediatorFalso mediador)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        return mediador;
    }

    private static ClienteDetalleDto Detalle(
        Guid id, string razonSocial, bool esCritico = false, string? notas = null,
        Guid? gestorCae = null, Guid? version = null) => new(
        id, razonSocial, "A-48.220.917", esCritico, notas,
        new DateTime(2019, 3, 4, 0, 0, 0, DateTimeKind.Utc), gestorCae, version ?? Guid.NewGuid());

    private static ResumenClienteDto Resumen(Guid id, int centros, int trabajadores) => new(
        id, "Refrielectric S.A.", "A-48.220.917", false,
        new DateTime(2019, 3, 4, 0, 0, 0, DateTimeKind.Utc), null, centros, trabajadores);

    private IRenderedComponent<ClienteWorkspacePanel> Renderizar(
        Guid id, string pestana = "informacion", Action<string>? alCambiarPestana = null) =>
        Render<ClienteWorkspacePanel>(p => p
            .Add(x => x.EntidadId, id)
            .Add(x => x.PestanaActiva, pestana)
            .Add(x => x.PestanaActivaChanged, EventCallback.Factory.Create<string>(this, v => alCambiarPestana?.Invoke(v))));

    private static IElement Boton(IRenderedComponent<ClienteWorkspacePanel> cut, string texto) =>
        cut.FindAll("button").Where(b => b.TextContent.Trim() == texto)
            .Should().ContainSingle($"tiene que haber exactamente un botón «{texto}»").Subject;

    private static string Celda(IRenderedComponent<ClienteWorkspacePanel> cut, string rotulo) =>
        cut.FindAll(".celda-info-cliente-360")
            .Where(c => c.QuerySelector("span")?.TextContent.Trim() == rotulo)
            .Select(c => c.QuerySelector("strong")?.TextContent.Trim() ?? string.Empty)
            .Should().ContainSingle($"la rejilla tiene que tener exactamente una celda «{rotulo}»").Subject;

    /// <summary>
    /// La píldora de recuento de una pestaña, o null si esa pestaña no lleva
    /// ninguna. Se lee del span propio y no del texto del botón: así el
    /// recuento se distingue de la etiqueta en vez de compararse pegado a ella.
    /// </summary>
    private static string? Contador(IRenderedComponent<ClienteWorkspacePanel> cut, string etiqueta) =>
        cut.FindAll("[role=tab]")
            .Where(t => t.TextContent.Trim().StartsWith(etiqueta, StringComparison.Ordinal))
            .Should().ContainSingle($"tiene que haber exactamente una pestaña «{etiqueta}»").Subject
            .QuerySelector(".pestanas-contador")?.TextContent.Trim();

    /// <summary>CampoTexto y CampoTextarea escuchan oninput, no onchange.</summary>
    private static Task EscribirAsync(IElement campo, string texto) =>
        campo.InputAsync(new ChangeEventArgs { Value = texto });

    private Task InvokeAsync(Action accion) => Renderer.Dispatcher.InvokeAsync(accion);

    // ─────────── Cabecera ───────────

    [Fact]
    public void La_cabecera_dice_que_es_un_Cliente_empresarial_y_cuantos_centros_y_trabajadores_tiene()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Refrielectric S.A.", esCritico: true);
        mediador.Resumenes[id] = Resumen(id, centros: 3, trabajadores: 42);

        var cut = Renderizar(id);

        var cabecera = cut.Find(".cabecera-cliente-360");
        cabecera.QuerySelector("h2")!.TextContent.Trim().Should().Be("Refrielectric S.A.");
        cabecera.QuerySelector(".kicker-cliente-360")!.TextContent.Trim().Should().Be("Cliente empresarial",
            "«Cliente» a secas no distingue al Cliente comercial TALVEG del Cliente empresarial de la relación");
        cabecera.QuerySelectorAll(".badge").Select(b => b.TextContent.Trim()).Should().Equal(["Crítico"]);
        cabecera.TextContent.Should().Contain("3 centros · 42 trabajadores");
    }

    [Fact]
    public void La_cabecera_no_pinta_cumplimiento_ni_vencidos_porque_ningun_DTO_los_trae_para_un_Cliente_empresarial()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Refrielectric S.A.");
        mediador.Resumenes[id] = Resumen(id, centros: 3, trabajadores: 42);

        var cut = Renderizar(id);

        var cabecera = cut.Find(".cabecera-cliente-360");
        cabecera.TextContent.Should().NotContain("cumplimiento",
            "no existe porcentaje de cumplimiento por Cliente empresarial: vive en CentroListaDto");
        cabecera.TextContent.Should().NotContain("vencidos",
            "el recuento por estado documental solo lo produce ObtenerClientesQuery, y es el del estado peor");
    }

    /// <summary>
    /// Un fallo no se disfraza de vacío: si el resumen no llega, la cabecera
    /// calla los recuentos en vez de afirmar «0 centros · 0 trabajadores»,
    /// que es una medición que nadie hizo.
    /// </summary>
    [Fact]
    public void Si_el_resumen_falla_la_cabecera_calla_los_recuentos_en_vez_de_pintar_ceros()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso
        {
            Lanzar = p => p is ObtenerResumenClienteQuery
        });
        mediador.Detalles[id] = Detalle(id, "Refrielectric S.A.");

        var cut = Renderizar(id);

        var cabecera = cut.Find(".cabecera-cliente-360");
        cabecera.QuerySelector("h2")!.TextContent.Trim().Should().Be("Refrielectric S.A.",
            "el fallo del resumen no tumba la ficha");
        cabecera.QuerySelectorAll(".recuentos-cliente-360").Should().BeEmpty();
        cabecera.TextContent.Should().NotContain("0 centros").And.NotContain("0 trabajadores");
    }

    // ─────────── Pestañas ───────────

    [Fact]
    public void Solo_las_tres_pestanas_cuyas_listas_carga_el_panel_llevan_recuento()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Refrielectric S.A.");
        mediador.Resumenes[id] = Resumen(id, 3, 42);
        mediador.Empresas[id] =
        [
            new(Guid.NewGuid(), "Montajes Ebro S.L.", "B-50.331.406"),
            new(Guid.NewGuid(), "Aislamientos Nervión S.L.", "B-95.410.882"),
            new(Guid.NewGuid(), "Elecnor Instalaciones S.A.U.", "A-28.774.209"),
            new(Guid.NewGuid(), "Grúas Aldapa S.L.", "B-01.339.702")
        ];
        mediador.Subcontratas[id] =
        [
            new(Guid.NewGuid(), "Pinturas Lauburu S.A."),
            new(Guid.NewGuid(), "Andamios Deusto S.L.")
        ];
        mediador.Centros[id] =
        [
            new(Guid.NewGuid(), "Centro Norte", "Montajes Ebro S.L."),
            new(Guid.NewGuid(), "Planta Zaragoza", "Elecnor Instalaciones S.A.U."),
            new(Guid.NewGuid(), "Nave logística Tudela", "Grúas Aldapa S.L.")
        ];
        // Otro Cliente empresarial con empresas: si el panel no filtrara por su Id, las contaría.
        mediador.Empresas[Guid.NewGuid()] = [new(Guid.NewGuid(), "Ajena S.L.", null)];

        var cut = Renderizar(id);

        cut.FindAll("[role=tab]").Should().HaveCount(9);
        Contador(cut, "Empresas").Should().Be("4 empresas");
        Contador(cut, "Subcontratas").Should().Be("2 subcontratas");
        Contador(cut, "Centros").Should().Be("3 centros");
        Contador(cut, "Blindaje 42.1").Should().BeNull("su recuento lo sabe PestanaBlindaje42, no este panel");
        Contador(cut, "Documentación").Should().BeNull("su recuento lo sabe PestanaDocumentacion, no este panel");
        Contador(cut, "Información").Should().BeNull();
        Contador(cut, "Agenda").Should().BeNull();
        Contador(cut, "Actividad").Should().BeNull();
        Contador(cut, "Notas").Should().BeNull();

        mediador.Enviadas.OfType<ObtenerEmpresasDeClienteQuery>().Should().ContainSingle()
            .Which.ClienteId.Should().Be(id);
    }

    [Fact]
    public void Un_recuento_de_uno_va_en_singular()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Refrielectric S.A.");
        mediador.Resumenes[id] = Resumen(id, 1, 1);
        mediador.Empresas[id] = [new(Guid.NewGuid(), "Montajes Ebro S.L.", "B-50.331.406")];
        mediador.Centros[id] = [new(Guid.NewGuid(), "Centro Norte", "Montajes Ebro S.L.")];

        var cut = Renderizar(id);

        Contador(cut, "Empresas").Should().Be("1 empresa");
        Contador(cut, "Centros").Should().Be("1 centro");
        Contador(cut, "Subcontratas").Should().Be("0 subcontratas",
            "la consulta respondió que no hay ninguna: cero medido no es cero inventado");
        cut.Find(".cabecera-cliente-360").TextContent.Should().Contain("1 centro · 1 trabajador");
    }

    // ─────────── Información ───────────

    [Fact]
    public void La_rejilla_de_informacion_ensena_solo_los_campos_que_el_DTO_trae()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Refrielectric S.A.", esCritico: true, gestorCae: Guid.NewGuid());
        mediador.Resumenes[id] = Resumen(id, 3, 42);

        var cut = Renderizar(id);

        Celda(cut, "Razón social").Should().Be("Refrielectric S.A.");
        Celda(cut, "CIF").Should().Be("A-48.220.917");
        Celda(cut, "Crítico").Should().Be("Sí");
        Celda(cut, "Alta").Should().Be("04/03/2019");

        var rotulos = cut.FindAll(".celda-info-cliente-360")
            .Select(c => c.QuerySelector("span")!.TextContent.Trim()).ToList();
        rotulos.Should().NotContain("Alias").And.NotContain("Domicilio",
            "no hay columna para ninguno de los dos");
        rotulos.Should().NotContain("Portal principal",
            "decisión ya tomada en ObtenerResumenClienteQuery: el portal es del Centro");
        rotulos.Should().NotContain("Cumplimiento");
        rotulos.Should().NotContain("Gestor CAE").And.NotContain("Ejecutivo",
            "leer la propiedad legacy que nombra a esa persona haría crecer la deuda que "
            + "TerminologiaCanonicaTests tiene congelada: la celda espera al renombrado");
    }

    // ─────────── Editar identidad ───────────

    [Fact]
    public async Task Guardar_la_identidad_reenvia_la_nota_vigente_para_no_borrarla()
    {
        var id = Guid.NewGuid();
        var version = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Refrielectric S.A.", notas: "Carmen prefiere antes de las 10:00.", version: version);
        mediador.Resumenes[id] = Resumen(id, 3, 42);

        var cut = Renderizar(id);
        await Boton(cut, "Editar identidad").ClickAsync(new MouseEventArgs());

        await EscribirAsync(cut.FindAll("input[type=text]")[0], "Refrielectric Sociedad Anónima");
        await Boton(cut, "Guardar").ClickAsync(new MouseEventArgs());

        var comando = mediador.Enviadas.OfType<EditarClienteCommand>().Should().ContainSingle().Subject;
        comando.RazonSocial.Should().Be("Refrielectric Sociedad Anónima");
        comando.Notas.Should().Be("Carmen prefiere antes de las 10:00.",
            "EditarClienteCommand reemplaza el registro completo: sin reenviarla, editar la identidad borraría la nota");
        comando.Version.Should().Be(version, "sin Version el comando no detecta la edición concurrente");
    }

    // ─────────── Notas ───────────

    [Fact]
    public async Task Guardar_la_nota_reenvia_la_identidad_vigente_y_la_Version()
    {
        var id = Guid.NewGuid();
        var version = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Refrielectric S.A.", esCritico: true, version: version);
        mediador.Resumenes[id] = Resumen(id, 3, 42);

        var cut = Renderizar(id, "notas");
        await EscribirAsync(cut.Find("textarea"), "El acceso al Centro Norte exige formación de altura.");
        await Boton(cut, "Guardar nota").ClickAsync(new MouseEventArgs());

        var comando = mediador.Enviadas.OfType<EditarClienteCommand>().Should().ContainSingle().Subject;
        comando.Notas.Should().Be("El acceso al Centro Norte exige formación de altura.");
        comando.RazonSocial.Should().Be("Refrielectric S.A.", "no hay comando de nota: guardar la nota no puede pisar la identidad");
        comando.Cif.Should().Be("A-48.220.917");
        comando.EsCritico.Should().BeTrue();
        comando.Version.Should().Be(version);
        Services.GetRequiredService<ToastService>().Mensajes.Should()
            .ContainSingle(m => m.Mensaje == "Nota de Refrielectric S.A. guardada." && m.Tono == TonoToast.Exito);
    }

    /// <summary>
    /// Tras un guardado con éxito la Version cambió en servidor. Sin recargar
    /// la cabecera, el segundo guardado iría con la Version vieja y el comando
    /// lo rechazaría por concurrencia.
    /// </summary>
    [Fact]
    public async Task Tras_guardar_la_nota_el_siguiente_guardado_va_con_la_Version_nueva()
    {
        var id = Guid.NewGuid();
        var primeraVersion = Guid.NewGuid();
        var segundaVersion = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Refrielectric S.A.", version: primeraVersion);
        mediador.Resumenes[id] = Resumen(id, 3, 42);

        var cut = Renderizar(id, "notas");
        await EscribirAsync(cut.Find("textarea"), "Primera.");
        // El servidor sube la Version al guardar: la recarga tiene que traerla.
        mediador.Detalles[id] = Detalle(id, "Refrielectric S.A.", notas: "Primera.", version: segundaVersion);
        await Boton(cut, "Guardar nota").ClickAsync(new MouseEventArgs());

        await EscribirAsync(cut.Find("textarea"), "Segunda.");
        await Boton(cut, "Guardar nota").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EditarClienteCommand>().Select(c => c.Version).Should()
            .Equal([primeraVersion, segundaVersion],
                "el segundo guardado usa la Version que trajo la recarga, no la de la primera carga");
    }

    /// <summary>
    /// «Guardar nota» es un Boton normal, no pasa por DialogoConfirmacion (que
    /// ya trae su propia guarda de reentrada): la única guarda contra el doble
    /// clic es la del panel, y comprueba al entrar — el botón deshabilitado no
    /// basta porque el clic ya viajaba cuando se deshabilitó.
    /// </summary>
    [Fact]
    public async Task Un_doble_clic_en_guardar_la_nota_manda_un_solo_comando()
    {
        var id = Guid.NewGuid();
        var comando = new TaskCompletionSource();
        var mediador = Registrar(new MediatorFalso
        {
            Retener = p => p is EditarClienteCommand ? comando.Task : null
        });
        mediador.Detalles[id] = Detalle(id, "Refrielectric S.A.");
        mediador.Resumenes[id] = Resumen(id, 3, 42);

        var cut = Renderizar(id, "notas");
        await EscribirAsync(cut.Find("textarea"), "Una nota.");

        // Sin await: el comando está retenido.
        var primero = Boton(cut, "Guardar nota").ClickAsync(new MouseEventArgs());
        var segundo = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Guardar nota").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EditarClienteCommand>().Should().ContainSingle();

        await InvokeAsync(() => comando.SetResult());
        await primero;
        await segundo;

        mediador.Enviadas.OfType<EditarClienteCommand>().Should().ContainSingle();
    }

    [Fact]
    public async Task Si_el_comando_rechaza_la_nota_no_se_dice_que_se_guardo()
    {
        const string motivo = "Ya existe otro cliente con ese CIF.";
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso
        {
            ResultadoEditar = Result.Fallo(Error.Crear("Cliente.CifDuplicado", motivo))
        });
        mediador.Detalles[id] = Detalle(id, "Refrielectric S.A.");
        mediador.Resumenes[id] = Resumen(id, 3, 42);

        var cut = Renderizar(id, "notas");
        await EscribirAsync(cut.Find("textarea"), "Una nota.");
        await Boton(cut, "Guardar nota").ClickAsync(new MouseEventArgs());

        cut.Find(".alerta-formulario").TextContent.Trim().Should().Be(motivo,
            "en el formulario el motivo va tal cual: está dentro de la ficha, con su razón social encima, "
            + "así que nombrarla aquí sería ruido — el nombre es del canal aviso, no de todos");
        Services.GetRequiredService<ToastService>().Mensajes.Should().NotContain(m => m.Tono == TonoToast.Exito,
            "un Result fallido no se disfraza de éxito");
        mediador.Enviadas.OfType<ObtenerClientePorIdQuery>().Should().ContainSingle(
            "un rechazo no recarga la cabecera: solo la carga de apertura pidió el detalle");
    }

    /// <summary>
    /// El otro lado de la regla del canal: si la ficha ya es otra, el
    /// formulario donde iba el motivo no existe, así que el motivo se va al
    /// aviso — y ahí sí nombra de quién era, porque el aviso se lee sobre la
    /// ficha nueva.
    /// </summary>
    [Fact]
    public async Task Si_la_nota_de_una_ficha_relevada_falla_el_motivo_se_va_al_aviso_nombrandola()
    {
        const string motivo = "Ya existe otro cliente con ese CIF.";
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var guardadoDeA = new TaskCompletionSource();
        var mediador = Registrar(new MediatorFalso
        {
            ResultadoEditar = Result.Fallo(Error.Crear("Cliente.CifDuplicado", motivo)),
            Retener = p => p is EditarClienteCommand ? guardadoDeA.Task : null
        });
        mediador.Detalles[a] = Detalle(a, "Refrielectric S.A.");
        mediador.Detalles[b] = Detalle(b, "Montajes Ebro S.L.");
        mediador.Resumenes[a] = Resumen(a, 3, 42);
        mediador.Resumenes[b] = Resumen(b, 2, 18);

        var cut = Renderizar(a, "notas");
        await EscribirAsync(cut.Find("textarea"), "Una nota de A.");
        // Sin await: el comando de A queda retenido.
        var guardado = Boton(cut, "Guardar nota").ClickAsync(new MouseEventArgs());

        cut.Render(p => p.Add(x => x.EntidadId, b).Add(x => x.PestanaActiva, "notas"));

        await InvokeAsync(() => guardadoDeA.SetResult());
        await guardado;

        var aviso = Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle().Subject;
        aviso.Tono.Should().Be(TonoToast.Error);
        aviso.Mensaje.Should().Be($"No pudimos guardar la nota de Refrielectric S.A.: {motivo}",
            "en pantalla está B: sin el nombre, el motivo se leería como que es la nota de B la que falló");
        cut.FindAll(".alerta-formulario").Should().BeEmpty(
            "el formulario de B no hereda el error de A");
    }

    // ─────────── Dar de baja ───────────

    [Fact]
    public async Task Dar_de_baja_pide_confirmacion_con_su_efecto_y_despues_vuelve_al_nivel_anterior_de_la_pila()
    {
        var id = Guid.NewGuid();
        var centro = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Refrielectric S.A.");
        mediador.Resumenes[id] = Resumen(id, 3, 42);
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();
        await workspace.AbrirAsync(EntidadWorkspace.Centro, centro, "Centro Norte", "informacion");
        await workspace.NavegarAAsync(EntidadWorkspace.Cliente, id, "Refrielectric S.A.", "informacion");

        var cut = Renderizar(id);
        await Boton(cut, "Dar de baja").ClickAsync(new MouseEventArgs());

        var dialogo = cut.Find("[role=dialog]");
        dialogo.TextContent.Should().Contain("¿Dar de baja a Refrielectric S.A.?")
            .And.Contain("deja de aparecer en las listas")
            .And.Contain("Si todavía tiene centros activos no se da de baja");
        mediador.Enviadas.OfType<EliminarClienteCommand>().Should().BeEmpty("abrir el diálogo no da de baja");

        await cut.Find("[role=dialog] .modal-pie").QuerySelectorAll("button")
            .Single(b => b.TextContent.Trim() == "Dar de baja").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EliminarClienteCommand>().Select(c => c.Id).Should().Equal([id]);
        workspace.Pila.Should().ContainSingle("tras la baja la ficha no tiene nada que enseñar y se vuelve al nivel anterior");
        workspace.FrameActual!.Tipo.Should().Be(EntidadWorkspace.Centro);
        Services.GetRequiredService<ToastService>().Mensajes.Should()
            .ContainSingle(m => m.Mensaje == "Refrielectric S.A. se dio de baja." && m.Tono == TonoToast.Exito);
    }

    [Fact]
    public async Task Si_el_comando_rechaza_la_baja_se_ensena_su_motivo_y_la_ficha_sigue_abierta()
    {
        const string motivo = "No puedes eliminar un cliente con centros activos. Da de baja sus centros primero.";
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso
        {
            ResultadoEliminar = Result.Fallo(Error.Crear("Cliente.TieneCentrosActivos", motivo))
        });
        mediador.Detalles[id] = Detalle(id, "Refrielectric S.A.");
        mediador.Resumenes[id] = Resumen(id, 3, 42);
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();
        await workspace.AbrirAsync(EntidadWorkspace.Cliente, id, "Refrielectric S.A.", "informacion");

        var cut = Renderizar(id);
        await Boton(cut, "Dar de baja").ClickAsync(new MouseEventArgs());
        await cut.Find("[role=dialog] .modal-pie").QuerySelectorAll("button")
            .Single(b => b.TextContent.Trim() == "Dar de baja").ClickAsync(new MouseEventArgs());

        Services.GetRequiredService<ToastService>().Mensajes.Should()
            .ContainSingle(m => m.Mensaje == $"Refrielectric S.A.: {motivo}" && m.Tono == TonoToast.Error,
                "el motivo va nombrado: el aviso es global y puede leerse sobre otra ficha");
        workspace.FrameActual.Should().NotBeNull("un rechazo no cierra la ficha");
        workspace.FrameActual!.EntidadId.Should().Be(id);
        cut.FindAll("[role=dialog]").Should().BeEmpty("el diálogo se cierra y el motivo queda en el aviso");
    }

    /// <summary>
    /// Una ficha relevada no puede tocar la nueva, pero su desenlace tampoco se
    /// calla: el comando no lleva el token del ciclo —cerrar la ficha no
    /// deshace la baja— así que la baja ocurrió de verdad y se cuenta,
    /// nombrando de quién era. Lo que no puede hacer es cerrar la
    /// confirmación de otro ni sacar de su ficha.
    /// </summary>
    [Fact]
    public async Task La_baja_de_una_ficha_relevada_se_anuncia_nombrandola_y_no_toca_la_ficha_nueva()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var centro = Guid.NewGuid();
        var bajaDeA = new TaskCompletionSource();
        var mediador = Registrar(new MediatorFalso
        {
            Retener = p => p is EliminarClienteCommand c && c.Id == a ? bajaDeA.Task : null
        });
        mediador.Detalles[a] = Detalle(a, "Refrielectric S.A.");
        mediador.Detalles[b] = Detalle(b, "Montajes Ebro S.L.");
        mediador.Resumenes[a] = Resumen(a, 3, 42);
        mediador.Resumenes[b] = Resumen(b, 2, 18);
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();
        await workspace.AbrirAsync(EntidadWorkspace.Centro, centro, "Centro Norte", "informacion");
        await workspace.NavegarAAsync(EntidadWorkspace.Cliente, a, "Refrielectric S.A.", "informacion");

        var cut = Renderizar(a);
        await Boton(cut, "Dar de baja").ClickAsync(new MouseEventArgs());
        // Sin await: el comando de A queda retenido.
        var confirmacionDeA = cut.Find("[role=dialog] .modal-pie").QuerySelectorAll("button")
            .Single(x => x.TextContent.Trim() == "Dar de baja").ClickAsync(new MouseEventArgs());

        await workspace.NavegarAAsync(EntidadWorkspace.Cliente, b, "Montajes Ebro S.L.", "informacion");
        cut.Render(p => p.Add(x => x.EntidadId, b).Add(x => x.PestanaActiva, "informacion"));
        await Boton(cut, "Dar de baja").ClickAsync(new MouseEventArgs());
        cut.Find("[role=dialog]").TextContent.Should().Contain("Montajes Ebro S.L.",
            "la confirmación abierta ahora es la de B");

        await InvokeAsync(() => bajaDeA.SetResult());
        await confirmacionDeA;

        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle()
            .Which.Mensaje.Should().Be("Refrielectric S.A. se dio de baja.",
                "la baja ocurrió: se cuenta, y dice de quién era en vez de callarse por haber cambiado de ficha");
        cut.Find("[role=dialog]").TextContent.Should().Contain("Montajes Ebro S.L.",
            "el final de la baja de A no cierra la confirmación de B");
        mediador.Enviadas.OfType<EliminarClienteCommand>().Select(c => c.Id).Should().Equal([a],
            "abrir la confirmación de B no da de baja a B");
        workspace.FrameActual!.EntidadId.Should().Be(b, "la baja de A no saca de la ficha de B");
        workspace.Pila.Should().HaveCount(3);
    }

    [Fact]
    public async Task Si_la_baja_de_una_ficha_relevada_falla_el_motivo_dice_de_quien_es()
    {
        const string motivo = "No puedes eliminar un cliente con centros activos.";
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var bajaDeA = new TaskCompletionSource();
        var mediador = Registrar(new MediatorFalso
        {
            ResultadoEliminar = Result.Fallo(Error.Crear("Cliente.TieneCentrosActivos", motivo)),
            Retener = p => p is EliminarClienteCommand ? bajaDeA.Task : null
        });
        mediador.Detalles[a] = Detalle(a, "Refrielectric S.A.");
        mediador.Detalles[b] = Detalle(b, "Montajes Ebro S.L.");
        mediador.Resumenes[a] = Resumen(a, 3, 42);
        mediador.Resumenes[b] = Resumen(b, 2, 18);

        var cut = Renderizar(a);
        await Boton(cut, "Dar de baja").ClickAsync(new MouseEventArgs());
        var confirmacion = cut.Find("[role=dialog] .modal-pie").QuerySelectorAll("button")
            .Single(x => x.TextContent.Trim() == "Dar de baja").ClickAsync(new MouseEventArgs());

        cut.Render(p => p.Add(x => x.EntidadId, b).Add(x => x.PestanaActiva, "informacion"));

        await InvokeAsync(() => bajaDeA.SetResult());
        await confirmacion;

        var aviso = Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle().Subject;
        aviso.Tono.Should().Be(TonoToast.Error);
        aviso.Mensaje.Should().Be($"Refrielectric S.A.: {motivo}",
            "en pantalla está B: un motivo sin nombre se leería como que es B quien no se puede dar de baja");
    }

    /// <summary>
    /// La guarda de reentrada de la baja, aislada de la del diálogo.
    /// <see cref="DialogoConfirmacion"/> trae la suya
    /// (<c>if (EnProgreso || _confirmando) return;</c>), así que un doble clic
    /// del usuario NO llega dos veces a este panel y no puede demostrar nada
    /// sobre su guarda: la del diálogo tapa la del panel. Este caso entra por
    /// el <c>OnConfirmar</c> del hijo, que es el mismo punto por el que
    /// entraría cualquier otro llamador —incluida una futura acción sin
    /// diálogo—, y así observa la guarda del panel y solo la del panel.
    ///
    /// <para>
    /// Queda dicho lo que esto NO es: hoy no hay camino de interfaz que mande
    /// dos confirmaciones a la vez. La guarda no se retira porque el contrato
    /// exige que toda escritura compruebe al entrar, y porque la guarda del
    /// diálogo no es la de esta página — si mañana se cuelga «Dar de baja» de
    /// un botón directo, la del diálogo deja de estar delante.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Dos_confirmaciones_de_baja_a_la_vez_mandan_un_solo_comando()
    {
        var id = Guid.NewGuid();
        var comando = new TaskCompletionSource();
        var mediador = Registrar(new MediatorFalso
        {
            Retener = p => p is EliminarClienteCommand ? comando.Task : null
        });
        mediador.Detalles[id] = Detalle(id, "Refrielectric S.A.");
        mediador.Resumenes[id] = Resumen(id, 3, 42);
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();
        await workspace.AbrirAsync(EntidadWorkspace.Cliente, id, "Refrielectric S.A.", "informacion");

        var cut = Renderizar(id);
        await Boton(cut, "Dar de baja").ClickAsync(new MouseEventArgs());
        var dialogo = cut.FindComponent<DialogoConfirmacion>();

        // Sin await: las dos entradas quedan dentro del método a la vez.
        var primera = cut.InvokeAsync(() => dialogo.Instance.OnConfirmar.InvokeAsync());
        var segunda = cut.InvokeAsync(() => dialogo.Instance.OnConfirmar.InvokeAsync());

        mediador.Enviadas.OfType<EliminarClienteCommand>().Should().ContainSingle(
            "la guarda comprueba al entrar: la segunda entrada se va sin mandar nada");

        await InvokeAsync(() => comando.SetResult());
        await primera;
        await segunda;

        mediador.Enviadas.OfType<EliminarClienteCommand>().Should().ContainSingle();
    }

    // ─────────── Concurrencia y ciclo de vida ───────────

    [Fact]
    public async Task Abrir_otro_Cliente_empresarial_con_la_carga_del_primero_en_vuelo_no_pinta_el_primero()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var respuestaDeA = new TaskCompletionSource();
        var mediador = Registrar(new MediatorFalso
        {
            Retener = p => p is ObtenerClientePorIdQuery q && q.Id == a ? respuestaDeA.Task : null
        });
        mediador.Detalles[a] = Detalle(a, "Refrielectric S.A.");
        mediador.Detalles[b] = Detalle(b, "Montajes Ebro S.L.");
        mediador.Resumenes[a] = Resumen(a, 3, 42);
        mediador.Resumenes[b] = Resumen(b, 2, 18);

        var cut = Renderizar(a);
        cut.FindAll(".cabecera-cliente-360").Should().BeEmpty("la cabecera de A sigue en vuelo");

        cut.Render(p => p.Add(x => x.EntidadId, b).Add(x => x.PestanaActiva, "informacion"));
        cut.Find(".cabecera-cliente-360 h2").TextContent.Trim().Should().Be("Montajes Ebro S.L.");

        await InvokeAsync(() => respuestaDeA.SetResult());

        cut.Find(".cabecera-cliente-360 h2").TextContent.Trim().Should().Be("Montajes Ebro S.L.",
            "la respuesta de A llegó tarde y ya no es la vigente");
        cut.Find(".cabecera-cliente-360").TextContent.Should().Contain("2 centros · 18 trabajadores")
            .And.NotContain("42 trabajadores", "los recuentos de A no se arrastran a B");
        mediador.Enviadas.OfType<ObtenerEmpresasDeClienteQuery>().Select(q => q.ClienteId).Should().Equal([b],
            "la cadena de A se corta al llegar: no pide las empresas de una ficha que ya no se ve");
    }

    /// <summary>
    /// Nada preparado para un Cliente empresarial puede ejecutarse sobre otro:
    /// el borrador de la nota de A no puede aparecer —ni guardarse— en B.
    /// </summary>
    [Fact]
    public async Task Cambiar_de_Cliente_empresarial_tira_el_borrador_de_la_nota_del_anterior()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[a] = Detalle(a, "Refrielectric S.A.", notas: "Nota de Refrielectric.");
        mediador.Detalles[b] = Detalle(b, "Montajes Ebro S.L.", notas: "Nota de Ebro.");
        mediador.Resumenes[a] = Resumen(a, 3, 42);
        mediador.Resumenes[b] = Resumen(b, 2, 18);

        var cut = Renderizar(a, "notas");
        await EscribirAsync(cut.Find("textarea"), "Borrador sin guardar de Refrielectric.");

        cut.Render(p => p.Add(x => x.EntidadId, b).Add(x => x.PestanaActiva, "notas"));

        cut.Find("textarea").TextContent.Should().Be("Nota de Ebro.",
            "el borrador de A se tira: guardarlo aquí lo escribiría sobre B");
        cut.Markup.Should().NotContain("Borrador sin guardar de Refrielectric.");
    }

    [Fact]
    public async Task Cambiar_de_Cliente_empresarial_cierra_el_formulario_de_identidad_abierto()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[a] = Detalle(a, "Refrielectric S.A.");
        mediador.Detalles[b] = Detalle(b, "Montajes Ebro S.L.");
        mediador.Resumenes[a] = Resumen(a, 3, 42);
        mediador.Resumenes[b] = Resumen(b, 2, 18);

        var cut = Renderizar(a);
        await Boton(cut, "Editar identidad").ClickAsync(new MouseEventArgs());
        cut.FindAll("input[type=text]").Should().NotBeEmpty("el formulario está abierto");

        cut.Render(p => p.Add(x => x.EntidadId, b).Add(x => x.PestanaActiva, "informacion"));

        cut.FindAll("input[type=text]").Should().BeEmpty(
            "el formulario preparado para A no puede quedar abierto sobre B");
        Boton(cut, "Editar identidad");
    }

    [Fact]
    public async Task Cambiar_de_Cliente_empresarial_cierra_el_dialogo_de_baja_abierto()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[a] = Detalle(a, "Refrielectric S.A.");
        mediador.Detalles[b] = Detalle(b, "Montajes Ebro S.L.");
        mediador.Resumenes[a] = Resumen(a, 3, 42);
        mediador.Resumenes[b] = Resumen(b, 2, 18);

        var cut = Renderizar(a);
        await Boton(cut, "Dar de baja").ClickAsync(new MouseEventArgs());
        cut.Find("[role=dialog]").TextContent.Should().Contain("Refrielectric S.A.");

        cut.Render(p => p.Add(x => x.EntidadId, b).Add(x => x.PestanaActiva, "informacion"));

        cut.FindAll("[role=dialog]").Should().BeEmpty(
            "la confirmación preparada para A no puede quedar abierta sobre B, que daría de baja a otro");
    }

    /// <summary>
    /// La bandera de «operación en curso» se reinicia al cambiar de entidad,
    /// así que el <c>finally</c> del guardado de A tiene que comprobar la
    /// generación antes de apagarla: si no, al terminar A reabriría B a un
    /// segundo envío mientras su propio guardado sigue en vuelo.
    /// </summary>
    [Fact]
    public async Task El_final_del_guardado_del_primero_no_reabre_el_segundo_a_un_segundo_envio()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var guardadoDeA = new TaskCompletionSource();
        var guardadoDeB = new TaskCompletionSource();
        var mediador = Registrar(new MediatorFalso
        {
            Retener = p => p is EditarClienteCommand c
                ? (c.Id == a ? guardadoDeA.Task : guardadoDeB.Task)
                : null
        });
        mediador.Detalles[a] = Detalle(a, "Refrielectric S.A.");
        mediador.Detalles[b] = Detalle(b, "Montajes Ebro S.L.");
        mediador.Resumenes[a] = Resumen(a, 3, 42);
        mediador.Resumenes[b] = Resumen(b, 2, 18);

        var cut = Renderizar(a, "notas");
        await EscribirAsync(cut.Find("textarea"), "Nota de A.");
        // Sin await: el guardado de A queda retenido.
        var deA = Boton(cut, "Guardar nota").ClickAsync(new MouseEventArgs());

        cut.Render(p => p.Add(x => x.EntidadId, b).Add(x => x.PestanaActiva, "notas"));
        await EscribirAsync(cut.Find("textarea"), "Nota de B.");
        // Sin await: el guardado de B también queda retenido.
        var deB = Boton(cut, "Guardar nota").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EditarClienteCommand>().Select(c => c.Id).Should().Equal([a, b]);

        // Termina el de A mientras el de B sigue en vuelo.
        await InvokeAsync(() => guardadoDeA.SetResult());
        await deA;

        // El desenlace no se calla por haber cambiado de ficha: se anuncia
        // nombrando a A, que es de quien era. Lo que no puede es tocar B.
        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle()
            .Which.Mensaje.Should().Be("Nota de Refrielectric S.A. guardada.",
                "el guardado de A terminó cuando en pantalla ya estaba B: lo cuenta, y dice que era de A");

        // Si el finally de A hubiera apagado la bandera, este clic mandaría un
        // segundo comando de B. SIN await y con la cuenta tomada antes de
        // afirmar nada: medido, con la guarda abierta el clic manda un comando
        // que el mediador retiene, y esperarlo aquí —o fallar antes de liberar
        // las dos esperas— cuelga el test en vez de hacerlo fallar. La
        // instantánea se toma en el momento porque Send registra la petición
        // antes de esperar.
        var tercerClic = cut.FindAll("button").Single(x => x.TextContent.Trim() == "Guardar nota").ClickAsync(new MouseEventArgs());
        var comandosTrasElTercerClic = mediador.Enviadas.OfType<EditarClienteCommand>().Select(c => c.Id).ToList();

        await InvokeAsync(() => guardadoDeB.SetResult());
        await deB;
        await tercerClic;

        comandosTrasElTercerClic.Should().Equal([a, b],
            "el guardado de B sigue en vuelo: su guarda de reentrada no la apaga el final del de A");
    }

    /// <summary>
    /// Todas las CONSULTAS llevan el token del ciclo de vida. Los COMANDOS no,
    /// a propósito y como en SubcontrataWorkspacePanel: cerrar la ficha no
    /// deshace una escritura que el usuario ya pidió.
    /// </summary>
    [Fact]
    public async Task Las_consultas_llevan_el_token_del_ciclo_y_los_comandos_no()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Refrielectric S.A.");
        mediador.Resumenes[id] = Resumen(id, 3, 42);

        var cut = Renderizar(id, "notas");

        var tokensDeConsulta = mediador.Tokens
            .Where(t => t.Peticion is not EditarClienteCommand and not EliminarClienteCommand)
            .Select(t => t.Token).ToList();
        tokensDeConsulta.Should().NotBeEmpty();
        tokensDeConsulta.Should().AllSatisfy(t => t.CanBeCanceled.Should().BeTrue(
            "una consulta sin token cancelable seguiría ocupando el DbContext del circuito tras cerrar la ficha"));
        tokensDeConsulta.Distinct().Should().ContainSingle("todas comparten el token del ciclo de vida");

        await EscribirAsync(cut.Find("textarea"), "Una nota.");
        await Boton(cut, "Guardar nota").ClickAsync(new MouseEventArgs());

        mediador.Tokens.Where(t => t.Peticion is EditarClienteCommand)
            .Should().OnlyContain(t => !t.Token.CanBeCanceled,
                "cerrar la ficha no puede cancelar a medias una escritura ya pedida");

        var tokenDelCiclo = tokensDeConsulta[0];
        tokenDelCiclo.IsCancellationRequested.Should().BeFalse();

        await DisposeComponentsAsync();

        tokenDelCiclo.IsCancellationRequested.Should().BeTrue(
            "retirado el panel, lo que estuviera en vuelo se cancela");
    }

    [Fact]
    public async Task Retirar_el_panel_con_la_carga_en_vuelo_corta_la_cadena_de_consultas()
    {
        var id = Guid.NewGuid();
        var cabecera = new TaskCompletionSource();
        var mediador = Registrar(new MediatorFalso
        {
            Retener = p => p is ObtenerClientePorIdQuery ? cabecera.Task : null
        });
        mediador.Detalles[id] = Detalle(id, "Refrielectric S.A.");
        mediador.Resumenes[id] = Resumen(id, 3, 42);

        var cut = Renderizar(id);
        var instancia = cut.Instance;
        var campoGeneracion = typeof(ClienteWorkspacePanel).GetField("_generacion", BindingFlags.Instance | BindingFlags.NonPublic);
        campoGeneracion.Should().NotBeNull("el test lee la generación para comprobar que Dispose se ejecutó");
        var generacionAntes = (int)campoGeneracion!.GetValue(instancia)!;

        await DisposeComponentsAsync();

        ((int)campoGeneracion.GetValue(instancia)!).Should().BeGreaterThan(generacionAntes,
            "DisposeComponentsAsync tiene que haber llamado a Dispose del panel (cut.Dispose de bUnit 2.x no lo hace)");

        await InvokeAsync(() => cabecera.SetResult());

        mediador.Enviadas.OfType<ObtenerResumenClienteQuery>().Should().BeEmpty(
            "retirado el panel, la cabecera que llega tarde no sigue pidiendo datos de una ficha que ya no se ve");
        mediador.Enviadas.OfType<ObtenerEmpresasDeClienteQuery>().Should().BeEmpty();
    }

    // ─────────── Listas de relaciones ───────────

    [Fact]
    public async Task Una_empresa_de_la_lista_apila_su_Empresa_360_sin_sustituir_al_Cliente_empresarial()
    {
        var id = Guid.NewGuid();
        var empresa = new EmpresaDeClienteDto(Guid.NewGuid(), "Montajes Ebro S.L.", "B-50.331.406");
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Refrielectric S.A.");
        mediador.Resumenes[id] = Resumen(id, 3, 42);
        mediador.Empresas[id] = [empresa];
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();
        await workspace.AbrirAsync(EntidadWorkspace.Cliente, id, "Refrielectric S.A.", "empresas");

        var cut = Renderizar(id, "empresas");

        var filas = cut.FindAll(".workspace-fila-entidad").ToList();
        filas.Should().ContainSingle();
        filas[0].TextContent.Should().Contain("Montajes Ebro S.L.").And.Contain("B-50.331.406");

        await filas[0].ClickAsync(new MouseEventArgs());

        workspace.Pila.Should().HaveCount(2, "el Empresa 360 se apila sobre el Cliente empresarial, no lo sustituye");
        workspace.FrameActual!.Tipo.Should().Be(EntidadWorkspace.Empresa);
        workspace.FrameActual.EntidadId.Should().Be(empresa.Id);
    }

    [Fact]
    public void Cada_lista_vacia_dice_que_es_lo_que_no_hay_sin_confundirla_con_un_fallo()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Refrielectric S.A.");
        mediador.Resumenes[id] = Resumen(id, 0, 0);

        var cut = Renderizar(id, "centros");

        cut.Markup.Should().Contain("Sin centros")
            .And.Contain("todavía no tiene ningún Centro");
        cut.Markup.Should().NotContain("No pudimos cargar", "un vacío no es un fallo");
    }

    [Fact]
    public void Si_una_lista_falla_se_ofrece_reintentar_y_no_se_finge_que_esta_vacia()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso
        {
            Lanzar = p => p is ObtenerCentrosDeClienteQuery
        });
        mediador.Detalles[id] = Detalle(id, "Refrielectric S.A.");
        mediador.Resumenes[id] = Resumen(id, 3, 42);

        var cut = Renderizar(id, "centros");

        cut.Markup.Should().Contain("No pudimos cargar los centros").And.NotContain("Sin centros");
        Boton(cut, "Reintentar");
        Contador(cut, "Centros").Should().BeNull("sin lista no hay recuento que poner");
    }
}
