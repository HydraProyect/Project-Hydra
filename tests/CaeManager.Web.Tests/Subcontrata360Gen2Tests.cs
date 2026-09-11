using System.Reflection;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Centros;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Subcontratas.Commands.CambiarNivelServicioSubcontrata;
using CaeManager.Application.Subcontratas.Commands.EliminarSubcontrata;
using CaeManager.Application.Subcontratas.Queries.ObtenerCentrosConActividadDeSubcontrata;
using CaeManager.Application.Subcontratas.Queries.ObtenerCredencialAccesoSubcontrata;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontrataPorId;
using CaeManager.Application.Subcontratas.Queries.ObtenerSupervisionSubcontrata;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Subcontratas;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Subcontratas.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Subcontrata 360 (<see cref="SubcontrataWorkspacePanel"/>) contra su mockup
/// Gen 2 («Subcontrata 360 TALVEG.dc.html»). Prueban efectos: qué se ve, qué
/// consultas y comandos salen y con qué parámetros, qué le pasa a la pila del
/// Context Workspace. bUnit no evalúa CSS, así que ninguno afirma un estilo.
///
/// <para>
/// Las esperas se retienen con <see cref="TaskCompletionSource{TResult}"/>
/// SIN continuaciones asíncronas: al liberar dentro de <c>cut.InvokeAsync</c>,
/// la continuación del panel corre en línea en el dispatcher del renderer, y
/// al volver del <c>await</c> ya ha escrito (o descartado) lo que iba a
/// escribir. Sin eso, una comprobación negativa podría pasar solo por llegar
/// antes que la respuesta.
/// </para>
/// </summary>
public class Subcontrata360Gen2Tests : BunitContext
{
    /// <summary>BotonCopiar importa ./js/clipboard.js.</summary>
    public Subcontrata360Gen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    /// <summary>
    /// Responde según los parámetros de cada consulta —el Id de la
    /// subcontrata, la página pedida—, no por tipo: una consulta con el Id
    /// equivocado recibe lo que le corresponde a ese Id, y el test lo ve.
    /// </summary>
    private sealed class MediatorFalso : IMediator
    {
        public Dictionary<Guid, SubcontrataDetalleDto> Detalles { get; } = [];
        public Dictionary<Guid, List<TrabajadorListaDto>> Trabajadores { get; } = [];
        public Dictionary<Guid, List<CentroConActividadDto>> Centros { get; } = [];
        public Dictionary<Guid, SupervisionSubcontrataDto> Supervisiones { get; } = [];
        public Dictionary<Guid, CredencialAccesoSubcontrataDto> Credenciales { get; } = [];
        public List<ClienteSelectorDto> Clientes { get; } = [];
        public List<EmpresaSelectorDto> Empresas { get; } = [];
        public Result ResultadoEliminar { get; set; } = Result.Exito();

        /// <summary>Si devuelve una tarea, la respuesta espera a que se complete.</summary>
        public Func<object, Task?>? Retener { get; set; }

        public List<object> Enviadas { get; } = [];

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            if (Retener?.Invoke(request) is { } retenida)
                await retenida;
            return (TResponse)Responder(request)!;
        }

        private object? Responder(object request) => request switch
        {
            ObtenerSubcontrataPorIdQuery q => Detalles.GetValueOrDefault(q.Id),
            ObtenerTrabajadoresQuery q => Paginar(q),
            ObtenerCentrosConActividadDeSubcontrataQuery q => Centros.GetValueOrDefault(q.SubcontrataId) ?? [],
            ObtenerSupervisionSubcontrataQuery q => Supervisiones.GetValueOrDefault(q.SubcontrataId),
            ObtenerCredencialAccesoSubcontrataQuery q => Credenciales.GetValueOrDefault(q.SubcontrataId),
            ObtenerClientesParaSelectorQuery => Clientes,
            ObtenerEmpresasParaSelectorQuery => Empresas,
            EliminarSubcontrataCommand => ResultadoEliminar,
            CambiarNivelServicioSubcontrataCommand => Result.Exito(),
            _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
        };

        private ResultadoPaginado<TrabajadorListaDto> Paginar(ObtenerTrabajadoresQuery q)
        {
            var todos = q.SubcontrataId is { } id ? Trabajadores.GetValueOrDefault(id) ?? [] : [];
            var pagina = todos.Skip((q.Pagina - 1) * q.TamanoPagina).Take(q.TamanoPagina).ToList();
            return new ResultadoPaginado<TrabajadorListaDto>(pagina, todos.Count, q.Pagina, q.TamanoPagina);
        }

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

    private static SubcontrataDetalleDto Detalle(
        Guid id, string razonSocial, NivelServicioSubcontrata nivel = NivelServicioSubcontrata.Gestionada,
        Guid[]? clientes = null, Guid[]? empresas = null) => new(
        id, razonSocial, "A-48.007.615", new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc),
        clientes ?? [], empresas ?? [], Guid.NewGuid(), nivel);

    private static TrabajadorListaDto Trabajador(string nombre, string apellidos, EstadoDocumento? estado = null) =>
        new(Guid.NewGuid(), nombre, apellidos, "12884021K", "Pinturas Lauburu S.A.", estado);

    private static SupervisionTipoDto Tipo(string nombre, bool exigido, EstadoSupervision estado, DateOnly? verificadoEl = null) =>
        new(Guid.NewGuid(), nombre, exigido, estado,
            verificadoEl is { } fecha
                ? new UltimaVerificacionDto(Guid.NewGuid(), fecha, ResultadoVerificacionExterna.Valido, null, null, false, null)
                : null);

    private static SupervisionCentroDto CentroSupervisado(string nombre, params SupervisionTipoDto[] tipos) =>
        new(Guid.NewGuid(), nombre, "Refrielectric S.A.", tipos);

    private MediatorFalso Registrar(MediatorFalso mediador)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        return mediador;
    }

    private IRenderedComponent<SubcontrataWorkspacePanel> Renderizar(
        Guid id, string pestana = "informacion", Action<string>? alCambiarPestana = null) =>
        Render<SubcontrataWorkspacePanel>(p => p
            .Add(x => x.EntidadId, id)
            .Add(x => x.PestanaActiva, pestana)
            .Add(x => x.PestanaActivaChanged, EventCallback.Factory.Create<string>(this, v => alCambiarPestana?.Invoke(v))));

    private static IElement Boton(IRenderedComponent<SubcontrataWorkspacePanel> cut, string texto) =>
        cut.FindAll("button").Where(b => b.TextContent.Trim() == texto)
            .Should().ContainSingle($"tiene que haber exactamente un botón «{texto}»").Subject;

    private static string Celda(IRenderedComponent<SubcontrataWorkspacePanel> cut, string rotulo) =>
        cut.FindAll(".celda-info-subcontrata-360")
            .Where(c => c.QuerySelector("span")?.TextContent.Trim() == rotulo)
            .Select(c => c.QuerySelector("strong")?.TextContent.Trim() ?? string.Empty)
            .Should().ContainSingle($"la rejilla tiene que tener exactamente una celda «{rotulo}»").Subject;

    [Fact]
    public void La_cabecera_dice_quien_es_su_nivel_y_cuantos_centros_quedan_con_un_requisito_exigido_sin_verificar()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Pinturas Lauburu S.A.", NivelServicioSubcontrata.Supervisada);
        mediador.Trabajadores[id] = [Trabajador("Marco", "Vila"), Trabajador("Eider", "Lasa"), Trabajador("Rubén", "Ortiz")];
        mediador.Centros[id] = [new(Guid.NewGuid(), "Centro Norte", "Refrielectric S.A.", 2), new(Guid.NewGuid(), "Planta Zaragoza", "Refrielectric S.A.", 1)];
        mediador.Supervisiones[id] = new SupervisionSubcontrataDto(
        [
            CentroSupervisado("Centro Norte", Tipo("Certificado TGSS", exigido: true, EstadoSupervision.SinVerificar)),
            // Sin verificar, pero el centro NO lo exige: no cuenta.
            CentroSupervisado("Planta Zaragoza",
                Tipo("Seguro RC", exigido: false, EstadoSupervision.SinVerificar),
                Tipo("Certificado TGSS", exigido: true, EstadoSupervision.Vigente, new DateOnly(2026, 8, 11))),
            CentroSupervisado("Centro Sur",
                Tipo("Certificado TGSS", exigido: true, EstadoSupervision.SinVerificar),
                Tipo("Evaluación de riesgos", exigido: true, EstadoSupervision.SinVerificar)),
        ], []);

        var cut = Renderizar(id);

        var cabecera = cut.Find(".cabecera-subcontrata-360");
        cabecera.QuerySelector("h2")!.TextContent.Trim().Should().Be("Pinturas Lauburu S.A.");
        cabecera.TextContent.Should().Contain("Subcontrata").And.NotContain("nivel de subcontratación",
            "ningún DTO trae la profundidad en la cadena, y el mockup la pintaba");
        cabecera.QuerySelectorAll(".badge").Select(b => b.TextContent.Trim()).Should().Equal(
            ["Supervisada", "2 centros sin verificar"],
            "Norte y Sur tienen un requisito exigido sin verificar; Zaragoza solo uno que no se le exige");
        cabecera.TextContent.Should().Contain("3 trabajadores · 2 centros");
    }

    [Fact]
    public void Las_pestanas_con_lista_llevan_su_recuento_total_y_la_lista_dice_cuantos_ensena()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Pinturas Lauburu S.A.");
        mediador.Trabajadores[id] = Enumerable.Range(1, 70).Select(i => Trabajador($"Nombre{i:00}", $"Apellido{i:00}")).ToList();
        mediador.Centros[id] = [new(Guid.NewGuid(), "Centro Norte", "Refrielectric S.A.", 2), new(Guid.NewGuid(), "Planta Zaragoza", "Refrielectric S.A.", 1)];
        // Otra subcontrata con trabajadores: si el panel no filtrara por su Id, contaría estos.
        mediador.Trabajadores[Guid.NewGuid()] = [Trabajador("Ajeno", "De otra")];

        var cut = Renderizar(id, "trabajadores");

        var pestanas = cut.FindAll("[role=tab]").Select(t => t.TextContent.Trim()).ToList();
        pestanas.Should().Contain("Trabajadores (70)", "el recuento es el total, no los 50 de la página")
            .And.Contain("Centros (2)").And.Contain("Supervisión").And.Contain("Historial");
        cut.Markup.Should().Contain("Se muestran 50 de 70 trabajadores que puedes ver");
        cut.FindAll(".workspace-fila-entidad").Should().HaveCount(50);
        mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Should().ContainSingle()
            .Which.SubcontrataId.Should().Be(id);
    }

    [Fact]
    public async Task Abrir_otra_subcontrata_con_la_carga_de_la_primera_en_vuelo_no_pinta_la_primera()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var respuestaDeA = new TaskCompletionSource();
        var mediador = Registrar(new MediatorFalso
        {
            Retener = p => p is ObtenerSubcontrataPorIdQuery q && q.Id == a ? respuestaDeA.Task : null
        });
        mediador.Detalles[a] = Detalle(a, "Pinturas Lauburu S.A.");
        mediador.Detalles[b] = Detalle(b, "Andamios Deusto S.L.");

        var cut = Renderizar(a);
        cut.FindAll(".cabecera-subcontrata-360").Should().BeEmpty("la cabecera de A sigue en vuelo");

        cut.Render(p => p.Add(x => x.EntidadId, b).Add(x => x.PestanaActiva, "informacion"));
        cut.Find(".cabecera-subcontrata-360 h2").TextContent.Trim().Should().Be("Andamios Deusto S.L.");

        await cut.InvokeAsync(() => respuestaDeA.SetResult());

        cut.Find(".cabecera-subcontrata-360 h2").TextContent.Trim().Should().Be("Andamios Deusto S.L.",
            "la respuesta de A llegó tarde y ya no es la vigente");
        mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Select(q => q.SubcontrataId).Should().Equal([b],
            "la cadena de A se corta al llegar: no pide los trabajadores de una ficha que ya no se ve");
    }

    [Fact]
    public async Task Las_credenciales_pedidas_para_la_primera_no_se_ensenan_en_la_segunda()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var credencialDeA = new TaskCompletionSource();
        var mediador = Registrar(new MediatorFalso
        {
            Retener = p => p is ObtenerCredencialAccesoSubcontrataQuery q && q.SubcontrataId == a ? credencialDeA.Task : null
        });
        mediador.Detalles[a] = Detalle(a, "Pinturas Lauburu S.A.");
        mediador.Detalles[b] = Detalle(b, "Andamios Deusto S.L.");
        mediador.Credenciales[a] = new("app.dokify.net/acceso", null, "lauburu.prl", "Lauburu.2026", null);

        var cut = Renderizar(a);
        // Sin await: la consulta de A está retenida.
        var clic = Boton(cut, "Ver credenciales").ClickAsync(new MouseEventArgs());

        cut.Render(p => p.Add(x => x.EntidadId, b).Add(x => x.PestanaActiva, "informacion"));
        await cut.InvokeAsync(() => credencialDeA.SetResult());
        await clic;

        cut.Find(".cabecera-subcontrata-360 h2").TextContent.Trim().Should().Be("Andamios Deusto S.L.");
        cut.Markup.Should().NotContain("lauburu.prl", "son las credenciales de otra subcontrata")
            .And.NotContain("app.dokify.net/acceso");
        Boton(cut, "Ver credenciales");
    }

    [Fact]
    public async Task Las_credenciales_no_se_piden_al_abrir_y_la_contrasena_sale_enmascarada_hasta_revelarla()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Pinturas Lauburu S.A.");
        mediador.Credenciales[id] = new("app.dokify.net/acceso", null, "lauburu.prl", "Lauburu.2026", null);

        var cut = Renderizar(id);

        mediador.Enviadas.OfType<ObtenerCredencialAccesoSubcontrataQuery>().Should().BeEmpty(
            "la consulta devuelve la contraseña: solo viaja tras una acción explícita (DEC-53/DEC-62)");

        await Boton(cut, "Ver credenciales").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<ObtenerCredencialAccesoSubcontrataQuery>().Select(q => q.SubcontrataId).Should().Equal([id]);
        cut.Markup.Should().Contain("lauburu.prl").And.Contain("app.dokify.net/acceso")
            .And.NotContain("Lauburu.2026", "la contraseña llega enmascarada");

        await Boton(cut, "Revelar").ClickAsync(new MouseEventArgs());

        cut.Markup.Should().Contain("Lauburu.2026");
        Boton(cut, "Ocultar").GetAttribute("aria-pressed").Should().Be("true");
        cut.Markup.Should().NotContain("Queda registrado en Auditoría",
            "revelarla no escribe ningún registro de auditoría, y el mockup lo prometía");

        await Boton(cut, "Ocultar credenciales").ClickAsync(new MouseEventArgs());

        cut.Markup.Should().NotContain("lauburu.prl").And.NotContain("Lauburu.2026");
    }

    [Fact]
    public async Task Dar_de_baja_pide_confirmacion_con_su_efecto_y_despues_vuelve_al_nivel_anterior_de_la_pila()
    {
        var id = Guid.NewGuid();
        var empresa = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Pinturas Lauburu S.A.");
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();
        await workspace.AbrirAsync(EntidadWorkspace.Empresa, empresa, "Montajes Ebro S.L.", "informacion");
        await workspace.NavegarAAsync(EntidadWorkspace.Subcontrata, id, "Pinturas Lauburu S.A.", "informacion");

        var cut = Renderizar(id);
        await Boton(cut, "Dar de baja").ClickAsync(new MouseEventArgs());

        var dialogo = cut.Find("[role=dialog]");
        dialogo.TextContent.Should().Contain("¿Dar de baja a Pinturas Lauburu S.A.?")
            .And.Contain("deja de aparecer en las listas").And.Contain("Si todavía tiene trabajadores no se da de baja");
        mediador.Enviadas.OfType<EliminarSubcontrataCommand>().Should().BeEmpty("abrir el diálogo no da de baja");

        await cut.Find("[role=dialog] .modal-pie").QuerySelectorAll("button")
            .Single(b => b.TextContent.Trim() == "Dar de baja").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EliminarSubcontrataCommand>().Select(c => c.Id).Should().Equal([id]);
        workspace.Pila.Should().ContainSingle("tras la baja la ficha no tiene nada que enseñar y se vuelve al nivel anterior");
        workspace.FrameActual!.Tipo.Should().Be(EntidadWorkspace.Empresa);
        Services.GetRequiredService<ToastService>().Mensajes.Should()
            .ContainSingle(m => m.Mensaje == "Pinturas Lauburu S.A. se dio de baja." && m.Tono == TonoToast.Exito);
    }

    [Fact]
    public async Task Si_el_comando_rechaza_la_baja_se_ensena_su_motivo_y_la_ficha_sigue_abierta()
    {
        const string motivo = "No puedes eliminar una subcontrata con trabajadores. Da de baja a sus trabajadores primero.";
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso
        {
            ResultadoEliminar = Result.Fallo(Error.Crear("Subcontrata.TieneTrabajadores", motivo))
        });
        mediador.Detalles[id] = Detalle(id, "Pinturas Lauburu S.A.");
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();
        await workspace.AbrirAsync(EntidadWorkspace.Subcontrata, id, "Pinturas Lauburu S.A.", "informacion");

        var cut = Renderizar(id);
        await Boton(cut, "Dar de baja").ClickAsync(new MouseEventArgs());
        await cut.Find("[role=dialog] .modal-pie").QuerySelectorAll("button")
            .Single(b => b.TextContent.Trim() == "Dar de baja").ClickAsync(new MouseEventArgs());

        Services.GetRequiredService<ToastService>().Mensajes.Should()
            .ContainSingle(m => m.Mensaje == motivo && m.Tono == TonoToast.Error);
        workspace.FrameActual.Should().NotBeNull("un rechazo no cierra la ficha");
        workspace.FrameActual!.EntidadId.Should().Be(id);
        cut.FindAll("[role=dialog]").Should().BeEmpty("el diálogo se cierra y el motivo queda en el aviso");
    }

    /// <summary>
    /// «Cambiar a …» no pasa por DialogoConfirmacion (es reversible), así que
    /// la única guarda contra el doble clic es la del propio panel.
    /// </summary>
    [Fact]
    public async Task Un_doble_clic_en_cambiar_el_nivel_de_servicio_manda_un_solo_comando()
    {
        var id = Guid.NewGuid();
        var comando = new TaskCompletionSource();
        var mediador = Registrar(new MediatorFalso
        {
            Retener = p => p is CambiarNivelServicioSubcontrataCommand ? comando.Task : null
        });
        mediador.Detalles[id] = Detalle(id, "Pinturas Lauburu S.A.");

        var cut = Renderizar(id);
        // Sin await: el comando está retenido.
        var primero = Boton(cut, "Cambiar a Supervisada").ClickAsync(new MouseEventArgs());
        var segundo = cut.FindAll("button").Single(b => b.TextContent.Contains("Cambiar a Supervisada")).ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<CambiarNivelServicioSubcontrataCommand>().Should().ContainSingle()
            .Which.NivelServicio.Should().Be(NivelServicioSubcontrata.Supervisada);

        await cut.InvokeAsync(() => comando.SetResult());
        await primero;
        await segundo;

        mediador.Enviadas.OfType<CambiarNivelServicioSubcontrataCommand>().Should().ContainSingle();
    }

    [Fact]
    public async Task Retirar_el_panel_con_la_carga_en_vuelo_corta_la_cadena_de_consultas()
    {
        var id = Guid.NewGuid();
        var cabecera = new TaskCompletionSource();
        var mediador = Registrar(new MediatorFalso
        {
            Retener = p => p is ObtenerSubcontrataPorIdQuery ? cabecera.Task : null
        });
        mediador.Detalles[id] = Detalle(id, "Pinturas Lauburu S.A.");

        var cut = Renderizar(id);
        var instancia = cut.Instance;
        var campoGeneracion = typeof(SubcontrataWorkspacePanel).GetField("_generacion", BindingFlags.Instance | BindingFlags.NonPublic);
        campoGeneracion.Should().NotBeNull("el test lee la generación para comprobar que Dispose se ejecutó");
        var generacionAntes = (int)campoGeneracion!.GetValue(instancia)!;

        await DisposeComponentsAsync();

        ((int)campoGeneracion.GetValue(instancia)!).Should().BeGreaterThan(generacionAntes,
            "DisposeComponentsAsync tiene que haber llamado a Dispose del panel (cut.Dispose de bUnit 2.x no lo hace)");

        await InvokeAsync(() => cabecera.SetResult());

        mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Should().BeEmpty(
            "retirado el panel, la cabecera que llega tarde no sigue pidiendo datos de una ficha que ya no se ve");
        mediador.Enviadas.OfType<ObtenerSupervisionSubcontrataQuery>().Should().BeEmpty();
    }

    private Task InvokeAsync(Action accion) => Renderer.Dispatcher.InvokeAsync(accion);

    [Fact]
    public async Task Un_trabajador_de_la_lista_lleva_su_estado_documental_y_apila_su_Trabajador_360()
    {
        var id = Guid.NewGuid();
        var vencido = Trabajador("Rubén", "Ortiz", EstadoDocumento.Vencido);
        var sinDocumentos = Trabajador("Nerea", "Aguirre");
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Pinturas Lauburu S.A.");
        mediador.Trabajadores[id] = [vencido, sinDocumentos];
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();
        await workspace.AbrirAsync(EntidadWorkspace.Subcontrata, id, "Pinturas Lauburu S.A.", "trabajadores");

        var cut = Renderizar(id, "trabajadores");

        var filas = cut.FindAll(".workspace-fila-entidad").ToList();
        filas.Select(f => f.QuerySelector(".badge")?.TextContent.Trim()).Should().Equal(["Vencido", "Sin documentos"],
            "cada fila dice su estado documental, también la que no tiene ningún documento");

        await filas[0].ClickAsync(new MouseEventArgs());

        workspace.Pila.Should().HaveCount(2, "el Trabajador 360 se apila sobre la subcontrata, no la sustituye");
        workspace.FrameActual!.Tipo.Should().Be(EntidadWorkspace.Trabajador);
        workspace.FrameActual.EntidadId.Should().Be(vencido.Id);
    }

    [Fact]
    public async Task La_pestana_de_documentacion_dice_lo_que_no_hay_y_lleva_a_sus_trabajadores()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Pinturas Lauburu S.A.");
        string? pedida = null;

        var cut = Renderizar(id, "documentacion", v => pedida = v);
        cut.Markup.Should().Contain("Documentación no modelada a nivel de Subcontrata");

        await Boton(cut, "Ver sus trabajadores").ClickAsync(new MouseEventArgs());

        pedida.Should().Be("trabajadores");
    }

    [Fact]
    public void Informacion_nombra_sus_relaciones_y_cuenta_las_que_no_puede_nombrar()
    {
        var id = Guid.NewGuid();
        var refrielectric = Guid.NewGuid();
        var clienteSinNombre = Guid.NewGuid();
        var montajes = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Pinturas Lauburu S.A.", clientes: [refrielectric, clienteSinNombre], empresas: [montajes]);
        mediador.Clientes.Add(new(refrielectric, "Refrielectric S.A."));
        mediador.Empresas.Add(new(montajes, "Montajes Ebro S.L."));

        var cut = Renderizar(id);

        Celda(cut, "Clientes empresariales para los que trabaja").Should().Be("Refrielectric S.A. y 1 más",
            "un Id cuyo nombre no llega se cuenta, no se omite ni se inventa");
        Celda(cut, "Empresas a las que presta servicio").Should().Be("Montajes Ebro S.L.");
        Celda(cut, "CIF").Should().Be("A-48.007.615");
    }

    [Fact]
    public void Un_deep_link_a_supervision_abre_esa_pestana_con_la_fecha_de_la_ultima_verificacion()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Pinturas Lauburu S.A.");
        mediador.Supervisiones[id] = new SupervisionSubcontrataDto(
        [
            CentroSupervisado("Centro Norte", Tipo("Certificado TGSS", exigido: true, EstadoSupervision.Vigente, new DateOnly(2026, 7, 13))),
            CentroSupervisado("Planta Zaragoza", Tipo("Certificado TGSS", exigido: true, EstadoSupervision.Vigente, new DateOnly(2026, 8, 11))),
        ], []);

        var cut = Renderizar(id, "supervision");

        cut.Find("[role=tab][aria-selected='true']").TextContent.Trim().Should().Be("Supervisión");
        cut.Find(".pestanas-panel").TextContent.Should()
            .Contain("Última verificación registrada el 11/08/2026", "es la más reciente de todos los centros")
            .And.Contain("Centro Norte").And.Contain("Planta Zaragoza");
    }
}
