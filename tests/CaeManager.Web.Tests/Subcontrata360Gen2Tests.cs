using System.Reflection;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Centros;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Subcontratas.Commands.CambiarNivelServicioSubcontrata;
using CaeManager.Application.Subcontratas.Commands.EditarSubcontrata;
using CaeManager.Application.Subcontratas.Commands.EliminarSubcontrata;
using CaeManager.Application.Subcontratas.Commands.EliminarVerificacionExterna;
using CaeManager.Application.Subcontratas.Commands.GuardarCredencialAccesoSubcontrata;
using CaeManager.Application.Subcontratas.Commands.RegistrarVerificacionExterna;
using CaeManager.Application.Subcontratas.Queries.ObtenerCentrosConActividadDeSubcontrata;
using CaeManager.Application.Subcontratas.Queries.ObtenerCredencialAccesoSubcontrata;
using CaeManager.Application.Subcontratas.Queries.ObtenerCredencialAccesoSubcontrataSinContrasena;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontrataPorId;
using CaeManager.Application.Subcontratas.Queries.ObtenerSupervisionSubcontrata;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
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
using Microsoft.AspNetCore.Components.Forms;
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

        /// <summary>Catálogo de tipos de documento por ámbito: la consulta responde según el ámbito que pide.</summary>
        public Dictionary<AmbitoAplicacion, List<TipoDocumentoListaDto>> Tipos { get; } = [];
        public Result ResultadoRegistrarVerificacion { get; set; } = Result.Exito();
        public Result ResultadoEliminarVerificacion { get; set; } = Result.Exito();
        public Result ResultadoEditar { get; set; } = Result.Exito();
        public Result ResultadoGuardarCredenciales { get; set; } = Result.Exito();

        /// <summary>Si devuelve una tarea, la respuesta espera a que se complete.</summary>
        public Func<object, Task?>? Retener { get; set; }

        public List<object> Enviadas { get; } = [];

        /// <summary>El token con el que llegó cada petición, en el mismo orden que <see cref="Enviadas"/>.</summary>
        public List<(object Peticion, CancellationToken Token)> Tokens { get; } = [];

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            Tokens.Add((request, cancellationToken));
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
            ObtenerCredencialAccesoSubcontrataSinContrasenaQuery q => Credenciales.TryGetValue(q.SubcontrataId, out var c)
                ? new CredencialAccesoSubcontrataSinContrasenaDto(c.UrlAcceso, c.CampoEmpresa, c.Usuario, c.Notas)
                : null,
            ObtenerClientesParaSelectorQuery => Clientes,
            ObtenerEmpresasParaSelectorQuery => Empresas,
            ObtenerTiposDocumentoQuery q => q.AmbitoAplicacion is { } ambito
                ? Tipos.GetValueOrDefault(ambito) ?? []
                : Tipos.Values.SelectMany(t => t).ToList(),
            EliminarSubcontrataCommand => ResultadoEliminar,
            CambiarNivelServicioSubcontrataCommand => Result.Exito(),
            RegistrarVerificacionExternaSubcontrataCommand => ResultadoRegistrarVerificacion,
            EliminarVerificacionExternaSubcontrataCommand => ResultadoEliminarVerificacion,
            EditarSubcontrataCommand => ResultadoEditar,
            GuardarCredencialAccesoSubcontrataCommand => ResultadoGuardarCredenciales,
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

    // ------------------------------------------------ flujos conservados: utilidades

    private static TipoDocumentoListaDto TipoCatalogo(string nombre, AmbitoAplicacion ambito, int orden) => new(
        Guid.NewGuid(), nombre, null, false, orden, ambito, default, default,
        null, null, null, null, false, false, false, default, []);

    private static SupervisionTipoDto TipoVerificado(string nombre, Guid verificacionId, DateOnly fecha) =>
        new(Guid.NewGuid(), nombre, true, EstadoSupervision.Vigente,
            new UltimaVerificacionDto(verificacionId, fecha, ResultadoVerificacionExterna.Valido, null, null, false, null));

    /// <summary>CampoTexto, CampoSelect y CampoTextarea enlazan su &lt;label for&gt;: se localiza el control por la etiqueta que ve el usuario.</summary>
    private static IElement Control(IRenderedComponent<SubcontrataWorkspacePanel> cut, string etiqueta)
    {
        var id = cut.FindAll("label").Where(l => l.TextContent.Trim() == etiqueta)
            .Should().ContainSingle($"tiene que haber exactamente un campo «{etiqueta}»").Subject.GetAttribute("for");
        return cut.Find($"#{id}");
    }

    private static IElement Casilla(IRenderedComponent<SubcontrataWorkspacePanel> cut, string nombre) =>
        cut.FindAll("label.campo-checkbox").Where(l => l.TextContent.Trim() == nombre)
            .Should().ContainSingle($"tiene que haber exactamente una casilla «{nombre}»").Subject
            .QuerySelector("input[type=checkbox]")!;

    private static IElement BotonDelPie(IRenderedComponent<SubcontrataWorkspacePanel> cut, string selectorPie, string texto) =>
        cut.Find(selectorPie).QuerySelectorAll("button").Where(b => b.TextContent.Trim() == texto)
            .Should().ContainSingle($"el pie tiene que tener exactamente un botón «{texto}»").Subject;

    private static IElement ConfirmarEliminacion(IRenderedComponent<SubcontrataWorkspacePanel> cut) =>
        BotonDelPie(cut, "[role=dialog] .modal-pie", "Eliminar");

    private static IElement BotonEliminarVerificacion(IRenderedComponent<SubcontrataWorkspacePanel> cut, string tipo, string centro) =>
        cut.Find($"button[aria-label='Eliminar la verificación de {tipo} en {centro}']");

    private IReadOnlyList<ToastMensaje> Toasts => Services.GetRequiredService<ToastService>().Mensajes;

    private sealed record EscenaRegistro(Guid Id, Guid Norte, Guid Zaragoza, TipoDocumentoListaDto Seguro, MediatorFalso Mediador);

    /// <summary>
    /// Dos centros seleccionables —con uno solo el panel lo preselecciona y
    /// el test no distinguiría el elegido del primero— y dos tipos en el
    /// catálogo, repartidos entre los dos ámbitos que pide el panel.
    /// </summary>
    private EscenaRegistro PrepararRegistro(MediatorFalso mediador)
    {
        var id = Guid.NewGuid();
        var norte = Guid.NewGuid();
        var zaragoza = Guid.NewGuid();
        var seguro = TipoCatalogo("Seguro RC", AmbitoAplicacion.Empresa, 2);
        Registrar(mediador);
        mediador.Detalles[id] = Detalle(id, "Pinturas Lauburu S.A.", NivelServicioSubcontrata.Supervisada);
        mediador.Tipos[AmbitoAplicacion.Trabajador] = [TipoCatalogo("Certificado TGSS", AmbitoAplicacion.Trabajador, 1)];
        mediador.Tipos[AmbitoAplicacion.Empresa] = [seguro];
        mediador.Supervisiones[id] = new SupervisionSubcontrataDto([],
            [new(norte, "Centro Norte", "Refrielectric S.A."), new(zaragoza, "Planta Zaragoza", "Refrielectric S.A.")]);
        return new(id, norte, zaragoza, seguro, mediador);
    }

    /// <summary>Abre el formulario y elige Planta Zaragoza, «Seguro RC», «No válido», el 03/09/2026 y una captura como evidencia.</summary>
    private async Task<IRenderedComponent<SubcontrataWorkspacePanel>> RellenarRegistroAsync(EscenaRegistro escena)
    {
        var cut = Renderizar(escena.Id, "supervision");
        await Boton(cut, "+ Registrar verificación").ClickAsync(new MouseEventArgs());

        await Control(cut, "Centro").ChangeAsync(new ChangeEventArgs { Value = escena.Zaragoza.ToString() });
        await Control(cut, "Tipo de documento").ChangeAsync(new ChangeEventArgs { Value = escena.Seguro.Id.ToString() });
        await Control(cut, "Resultado").ChangeAsync(new ChangeEventArgs { Value = nameof(ResultadoVerificacionExterna.NoValido) });
        // CampoTexto notifica tras su rebote: InputAsync espera a que termine.
        await Control(cut, "Fecha de verificación").InputAsync(new ChangeEventArgs { Value = "2026-09-03" });
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary([7, 7, 7], "captura-portal.png"));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Adjunto: captura-portal.png"));
        return cut;
    }

    // ------------------------------------------------ registrar una verificación

    [Fact]
    public async Task Registrar_una_verificacion_envia_la_subcontrata_el_centro_el_tipo_y_la_evidencia_elegidos_y_refresca_la_supervision()
    {
        var escena = PrepararRegistro(new MediatorFalso());
        var cut = await RellenarRegistroAsync(escena);
        var supervisionesAntes = escena.Mediador.Enviadas.OfType<ObtenerSupervisionSubcontrataQuery>().Count();
        // Lo que devolverá la supervisión al volver a pedirla: si el panel no la refresca, no aparece.
        escena.Mediador.Supervisiones[escena.Id] = new SupervisionSubcontrataDto(
            [CentroSupervisado("Planta Zaragoza", Tipo("Seguro RC", exigido: true, EstadoSupervision.Vigente, new DateOnly(2026, 9, 3)))],
            [new(escena.Norte, "Centro Norte", "Refrielectric S.A."), new(escena.Zaragoza, "Planta Zaragoza", "Refrielectric S.A.")]);

        await BotonDelPie(cut, ".drawer-pie", "Registrar").ClickAsync(new MouseEventArgs());

        var comando = escena.Mediador.Enviadas.OfType<RegistrarVerificacionExternaSubcontrataCommand>().Should().ContainSingle().Subject;
        comando.SubcontrataId.Should().Be(escena.Id);
        comando.CentroId.Should().Be(escena.Zaragoza, "se eligió Planta Zaragoza, no el primer centro de la lista");
        comando.TipoDocumentoId.Should().Be(escena.Seguro.Id);
        comando.Resultado.Should().Be(ResultadoVerificacionExterna.NoValido);
        comando.FechaVerificacion.Should().Be(new DateOnly(2026, 9, 3));
        comando.EvidenciaContenido.Should().Equal([7, 7, 7]);
        comando.EvidenciaNombreArchivo.Should().Be("captura-portal.png");

        escena.Mediador.Enviadas.OfType<ObtenerSupervisionSubcontrataQuery>().Should().HaveCount(supervisionesAntes + 1)
            .And.Subject.Last().SubcontrataId.Should().Be(escena.Id);
        cut.FindAll(".drawer-panel").Should().BeEmpty("registrada, el formulario se cierra");
        cut.Find(".pestanas-panel").TextContent.Should().Contain("Última verificación registrada el 03/09/2026");
        Toasts.Should().ContainSingle(m => m.Mensaje == "Verificación registrada." && m.Tono == TonoToast.Exito);
    }

    [Fact]
    public async Task Si_el_registro_de_la_verificacion_falla_su_motivo_sale_en_el_formulario_que_sigue_abierto_y_se_puede_reintentar()
    {
        const string motivo = "Ese centro ya no está en tu cartera.";
        var escena = PrepararRegistro(new MediatorFalso
        {
            ResultadoRegistrarVerificacion = Result.Fallo(Error.Crear("VerificacionExterna.CentroFueraDeAlcance", motivo))
        });
        var cut = await RellenarRegistroAsync(escena);
        var supervisionesAntes = escena.Mediador.Enviadas.OfType<ObtenerSupervisionSubcontrataQuery>().Count();

        await BotonDelPie(cut, ".drawer-pie", "Registrar").ClickAsync(new MouseEventArgs());

        cut.Find(".drawer-panel [role=alert]").TextContent.Trim().Should().Be(motivo);
        BotonDelPie(cut, ".drawer-pie", "Registrar").HasAttribute("disabled").Should().BeFalse("el guardado terminó: el botón no se queda cargando");
        escena.Mediador.Enviadas.OfType<ObtenerSupervisionSubcontrataQuery>().Should().HaveCount(supervisionesAntes, "nada se registró: no hay qué refrescar");
        Toasts.Should().NotContain(m => m.Tono == TonoToast.Exito);

        escena.Mediador.ResultadoRegistrarVerificacion = Result.Exito();
        await BotonDelPie(cut, ".drawer-pie", "Registrar").ClickAsync(new MouseEventArgs());

        escena.Mediador.Enviadas.OfType<RegistrarVerificacionExternaSubcontrataCommand>().Select(c => (c.CentroId, c.EvidenciaNombreArchivo))
            .Should().Equal([(escena.Zaragoza, "captura-portal.png"), (escena.Zaragoza, "captura-portal.png")],
                "el reintento sale con lo que ya estaba elegido: el fallo no vació el formulario");
        cut.FindAll(".drawer-panel").Should().BeEmpty();
    }

    [Fact]
    public async Task Un_doble_clic_en_Registrar_manda_un_solo_comando()
    {
        var registro = new TaskCompletionSource();
        var escena = PrepararRegistro(new MediatorFalso
        {
            Retener = p => p is RegistrarVerificacionExternaSubcontrataCommand ? registro.Task : null
        });
        var cut = await RellenarRegistroAsync(escena);

        // Sin await: el comando está retenido.
        var primero = BotonDelPie(cut, ".drawer-pie", "Registrar").ClickAsync(new MouseEventArgs());
        var segundo = BotonDelPie(cut, ".drawer-pie", "Registrar").ClickAsync(new MouseEventArgs());

        escena.Mediador.Enviadas.OfType<RegistrarVerificacionExternaSubcontrataCommand>().Should().ContainSingle();

        await cut.InvokeAsync(() => registro.SetResult());
        await primero;
        await segundo;

        escena.Mediador.Enviadas.OfType<RegistrarVerificacionExternaSubcontrataCommand>().Should().ContainSingle();
    }

    // ------------------------------------------------ eliminar una verificación

    private sealed record EscenaEliminacion(Guid Id, Guid VerificacionNorte, Guid VerificacionZaragoza, MediatorFalso Mediador);

    private EscenaEliminacion PrepararEliminacion(MediatorFalso mediador)
    {
        var id = Guid.NewGuid();
        var norte = Guid.NewGuid();
        var zaragoza = Guid.NewGuid();
        Registrar(mediador);
        mediador.Detalles[id] = Detalle(id, "Pinturas Lauburu S.A.", NivelServicioSubcontrata.Supervisada);
        mediador.Supervisiones[id] = new SupervisionSubcontrataDto(
        [
            CentroSupervisado("Centro Norte", TipoVerificado("Certificado TGSS", norte, new DateOnly(2026, 7, 13))),
            CentroSupervisado("Planta Zaragoza", TipoVerificado("Certificado TGSS", zaragoza, new DateOnly(2026, 8, 11))),
        ], []);
        return new(id, norte, zaragoza, mediador);
    }

    [Fact]
    public void Cada_Eliminar_de_una_verificacion_dice_en_su_nombre_accesible_que_verificacion_y_de_que_centro_borra()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Pinturas Lauburu S.A.");
        mediador.Supervisiones[id] = new SupervisionSubcontrataDto(
        [
            CentroSupervisado("Centro Norte",
                TipoVerificado("Certificado TGSS", Guid.NewGuid(), new DateOnly(2026, 7, 13)),
                TipoVerificado("Seguro RC", Guid.NewGuid(), new DateOnly(2026, 7, 20)),
                // Sin verificación: no hay nada que eliminar y no lleva botón.
                Tipo("Evaluación de riesgos", exigido: true, EstadoSupervision.SinVerificar)),
            CentroSupervisado("Planta Zaragoza", TipoVerificado("Certificado TGSS", Guid.NewGuid(), new DateOnly(2026, 8, 11))),
        ], []);

        var cut = Renderizar(id, "supervision");

        cut.FindAll("button").Where(b => b.TextContent.Trim() == "Eliminar").Select(b => b.GetAttribute("aria-label")).Should().Equal(
            [
                "Eliminar la verificación de Certificado TGSS en Centro Norte",
                "Eliminar la verificación de Seguro RC en Centro Norte",
                "Eliminar la verificación de Certificado TGSS en Planta Zaragoza",
            ],
            "tres botones con el mismo texto visible: el nombre accesible es lo único que los distingue, y el tipo se repite entre centros");
    }

    [Fact]
    public async Task Eliminar_una_verificacion_pide_confirmacion_envia_su_id_y_refresca_la_supervision()
    {
        var escena = PrepararEliminacion(new MediatorFalso());
        var cut = Renderizar(escena.Id, "supervision");

        await BotonEliminarVerificacion(cut, "Certificado TGSS", "Planta Zaragoza").ClickAsync(new MouseEventArgs());

        cut.Find("[role=dialog]").TextContent.Should().Contain("¿Eliminar esta verificación?");
        escena.Mediador.Enviadas.OfType<EliminarVerificacionExternaSubcontrataCommand>().Should().BeEmpty("abrir el diálogo no elimina");

        var supervisionesAntes = escena.Mediador.Enviadas.OfType<ObtenerSupervisionSubcontrataQuery>().Count();
        escena.Mediador.Supervisiones[escena.Id] = new SupervisionSubcontrataDto(
            [CentroSupervisado("Centro Norte", TipoVerificado("Certificado TGSS", escena.VerificacionNorte, new DateOnly(2026, 7, 13)))], []);

        await ConfirmarEliminacion(cut).ClickAsync(new MouseEventArgs());

        escena.Mediador.Enviadas.OfType<EliminarVerificacionExternaSubcontrataCommand>().Select(c => c.Id)
            .Should().Equal([escena.VerificacionZaragoza], "es la del botón pulsado, no la de otro centro con el mismo tipo");
        escena.Mediador.Enviadas.OfType<ObtenerSupervisionSubcontrataQuery>().Should().HaveCount(supervisionesAntes + 1);
        cut.FindAll("[role=dialog]").Should().BeEmpty();
        cut.Find(".pestanas-panel").TextContent.Should().NotContain("Planta Zaragoza", "la supervisión refrescada ya no la trae");
        Toasts.Should().ContainSingle(m => m.Mensaje == "Verificación eliminada." && m.Tono == TonoToast.Exito);
    }

    [Fact]
    public async Task Si_eliminar_la_verificacion_falla_se_avisa_del_motivo_y_el_dialogo_queda_listo_para_reintentar()
    {
        const string motivo = "La verificación ya no existe.";
        var escena = PrepararEliminacion(new MediatorFalso
        {
            ResultadoEliminarVerificacion = Result.Fallo(Error.Crear("VerificacionExterna.NoEncontrada", motivo))
        });
        var cut = Renderizar(escena.Id, "supervision");
        await BotonEliminarVerificacion(cut, "Certificado TGSS", "Planta Zaragoza").ClickAsync(new MouseEventArgs());
        var supervisionesAntes = escena.Mediador.Enviadas.OfType<ObtenerSupervisionSubcontrataQuery>().Count();

        await ConfirmarEliminacion(cut).ClickAsync(new MouseEventArgs());

        Toasts.Should().ContainSingle(m => m.Mensaje == motivo && m.Tono == TonoToast.Error);
        ConfirmarEliminacion(cut).HasAttribute("disabled").Should().BeFalse("el comando terminó: el botón no se queda cargando");
        escena.Mediador.Enviadas.OfType<ObtenerSupervisionSubcontrataQuery>().Should().HaveCount(supervisionesAntes);

        escena.Mediador.ResultadoEliminarVerificacion = Result.Exito();
        await ConfirmarEliminacion(cut).ClickAsync(new MouseEventArgs());

        escena.Mediador.Enviadas.OfType<EliminarVerificacionExternaSubcontrataCommand>().Select(c => c.Id)
            .Should().Equal([escena.VerificacionZaragoza, escena.VerificacionZaragoza], "el reintento borra la misma verificación");
        cut.FindAll("[role=dialog]").Should().BeEmpty();
    }

    [Fact]
    public async Task Un_doble_clic_en_confirmar_la_eliminacion_manda_un_solo_comando()
    {
        var eliminacion = new TaskCompletionSource();
        var escena = PrepararEliminacion(new MediatorFalso
        {
            Retener = p => p is EliminarVerificacionExternaSubcontrataCommand ? eliminacion.Task : null
        });
        var cut = Renderizar(escena.Id, "supervision");
        await BotonEliminarVerificacion(cut, "Certificado TGSS", "Planta Zaragoza").ClickAsync(new MouseEventArgs());

        // Sin await: el comando está retenido.
        var primero = ConfirmarEliminacion(cut).ClickAsync(new MouseEventArgs());
        var segundo = ConfirmarEliminacion(cut).ClickAsync(new MouseEventArgs());

        escena.Mediador.Enviadas.OfType<EliminarVerificacionExternaSubcontrataCommand>().Should().ContainSingle();

        await cut.InvokeAsync(() => eliminacion.SetResult());
        await primero;
        await segundo;

        escena.Mediador.Enviadas.OfType<EliminarVerificacionExternaSubcontrataCommand>().Should().ContainSingle();
    }

    // ------------------------------------------------ editar identidad y credenciales

    private sealed record EscenaEdicion(SubcontrataDetalleDto Original, Guid Refrielectric, Guid Arrasate, Guid Montajes, MediatorFalso Mediador);

    private EscenaEdicion PrepararEdicion(MediatorFalso mediador)
    {
        var id = Guid.NewGuid();
        var refrielectric = Guid.NewGuid();
        var arrasate = Guid.NewGuid();
        var montajes = Guid.NewGuid();
        Registrar(mediador);
        var original = Detalle(id, "Pinturas Lauburu S.A.", clientes: [refrielectric]);
        mediador.Detalles[id] = original;
        mediador.Clientes.AddRange([new(refrielectric, "Refrielectric S.A."), new(arrasate, "Talleres Arrasate S.Coop.")]);
        mediador.Empresas.Add(new(montajes, "Montajes Ebro S.L."));
        mediador.Credenciales[id] = new("app.dokify.net/acceso", null, "lauburu.prl", null, null);
        return new(original, refrielectric, arrasate, montajes, mediador);
    }

    private async Task<IRenderedComponent<SubcontrataWorkspacePanel>> AbrirEdicionAsync(EscenaEdicion escena)
    {
        var cut = Renderizar(escena.Original.Id);
        await Boton(cut, "Editar identidad").ClickAsync(new MouseEventArgs());
        return cut;
    }

    [Fact]
    public async Task Guardar_la_edicion_envia_los_datos_editados_y_vuelve_a_la_lectura_con_la_cabecera_refrescada()
    {
        var escena = PrepararEdicion(new MediatorFalso());
        var cut = await AbrirEdicionAsync(escena);

        await Control(cut, "Razón social").InputAsync(new ChangeEventArgs { Value = "Pinturas Lauburu Norte S.A." });
        await Control(cut, "CIF").InputAsync(new ChangeEventArgs { Value = "A-48.007.616" });
        await Casilla(cut, "Talleres Arrasate S.Coop.").ChangeAsync(new ChangeEventArgs { Value = true });
        await Casilla(cut, "Montajes Ebro S.L.").ChangeAsync(new ChangeEventArgs { Value = true });
        // Lo que devolverá la cabecera al volver a pedirla.
        escena.Mediador.Detalles[escena.Original.Id] = Detalle(escena.Original.Id, "Pinturas Lauburu Norte S.A.");

        await Boton(cut, "Guardar").ClickAsync(new MouseEventArgs());

        var comando = escena.Mediador.Enviadas.OfType<EditarSubcontrataCommand>().Should().ContainSingle().Subject;
        comando.Id.Should().Be(escena.Original.Id);
        comando.RazonSocial.Should().Be("Pinturas Lauburu Norte S.A.");
        comando.Cif.Should().Be("A-48.007.616");
        comando.ClienteIds.Should().BeEquivalentTo([escena.Refrielectric, escena.Arrasate], "se conserva el que tenía y se añade el marcado");
        comando.EmpresaIds.Should().BeEquivalentTo([escena.Montajes]);
        comando.Version.Should().Be(escena.Original.Version, "la versión leída es la que protege contra una edición concurrente");

        cut.Find(".cabecera-subcontrata-360 h2").TextContent.Trim().Should().Be("Pinturas Lauburu Norte S.A.");
        cut.FindAll(".rejilla-info-subcontrata-360").Should().ContainSingle("guardado, se vuelve a la lectura");
        Toasts.Should().ContainSingle(m => m.Mensaje == "Subcontrata actualizada correctamente." && m.Tono == TonoToast.Exito);
    }

    [Fact]
    public async Task Si_guardar_la_edicion_falla_su_motivo_sale_en_el_formulario_y_se_sigue_editando_con_lo_escrito()
    {
        const string motivo = "Ya existe otra subcontrata con ese CIF.";
        var escena = PrepararEdicion(new MediatorFalso
        {
            ResultadoEditar = Result.Fallo(Error.Crear("Subcontrata.CifDuplicado", motivo))
        });
        var cut = await AbrirEdicionAsync(escena);
        await Control(cut, "Razón social").InputAsync(new ChangeEventArgs { Value = "Pinturas Lauburu Norte S.A." });

        await Boton(cut, "Guardar").ClickAsync(new MouseEventArgs());

        cut.FindAll(".alerta-formulario[role=alert]").Select(a => a.TextContent.Trim()).Should().Equal([motivo]);
        Boton(cut, "Guardar").HasAttribute("disabled").Should().BeFalse("el guardado terminó: el botón no se queda cargando");
        cut.FindAll(".rejilla-info-subcontrata-360").Should().BeEmpty("un rechazo no saca de la edición");
        escena.Mediador.Enviadas.OfType<ObtenerSubcontrataPorIdQuery>().Should().ContainSingle("nada cambió: la cabecera no se vuelve a pedir");
        Toasts.Should().NotContain(m => m.Tono == TonoToast.Exito);

        escena.Mediador.ResultadoEditar = Result.Exito();
        await Boton(cut, "Guardar").ClickAsync(new MouseEventArgs());

        escena.Mediador.Enviadas.OfType<EditarSubcontrataCommand>().Select(c => c.RazonSocial)
            .Should().Equal(["Pinturas Lauburu Norte S.A.", "Pinturas Lauburu Norte S.A."], "el reintento conserva lo escrito");
    }

    [Fact]
    public async Task Un_doble_clic_en_Guardar_la_edicion_manda_un_solo_comando()
    {
        var edicion = new TaskCompletionSource();
        var escena = PrepararEdicion(new MediatorFalso
        {
            Retener = p => p is EditarSubcontrataCommand ? edicion.Task : null
        });
        var cut = await AbrirEdicionAsync(escena);

        // Sin await: el comando está retenido.
        var primero = Boton(cut, "Guardar").ClickAsync(new MouseEventArgs());
        var segundo = Boton(cut, "Guardar").ClickAsync(new MouseEventArgs());

        escena.Mediador.Enviadas.OfType<EditarSubcontrataCommand>().Should().ContainSingle();

        await cut.InvokeAsync(() => edicion.SetResult());
        await primero;
        await segundo;

        escena.Mediador.Enviadas.OfType<EditarSubcontrataCommand>().Should().ContainSingle();
    }

    /// <summary>
    /// No afirma nada de la contraseña: cómo se precarga en la edición es un
    /// contrato que se decide en otra parte, y este test no debe fijarlo.
    /// </summary>
    [Fact]
    public async Task Guardar_las_credenciales_envia_lo_editado_y_conserva_lo_que_no_se_toco()
    {
        var escena = PrepararEdicion(new MediatorFalso());
        var cut = await AbrirEdicionAsync(escena);

        await Control(cut, "Usuario").InputAsync(new ChangeEventArgs { Value = "lauburu.admin" });
        await Control(cut, "Notas").InputAsync(new ChangeEventArgs { Value = "Portal renovado en septiembre" });

        await Boton(cut, "Guardar credenciales").ClickAsync(new MouseEventArgs());

        var comando = escena.Mediador.Enviadas.OfType<GuardarCredencialAccesoSubcontrataCommand>().Should().ContainSingle().Subject;
        comando.SubcontrataId.Should().Be(escena.Original.Id);
        comando.UrlAcceso.Should().Be("app.dokify.net/acceso", "precargada y sin tocar: se reenvía tal cual");
        comando.CampoEmpresa.Should().BeNull();
        comando.Usuario.Should().Be("lauburu.admin");
        comando.Notas.Should().Be("Portal renovado en septiembre");
        Toasts.Should().ContainSingle(m => m.Mensaje == "Credenciales guardadas correctamente." && m.Tono == TonoToast.Exito);
    }

    /// <summary>
    /// DEFECTO 2 (2026-09-11): el formulario de edición precargaba la
    /// contraseña real vía <c>ObtenerCredencialAccesoSubcontrataQuery</c> —la
    /// consulta marcada como <c>IConsultaDeSecretosDeTenant</c>—, así que
    /// viajaba al circuito y al DOM (enmascarada visualmente, pero presente)
    /// aunque nadie pulsara «Revelar». Ahora abrir la edición usa
    /// <c>ObtenerCredencialAccesoSubcontrataSinContrasenaQuery</c>, cuyo DTO
    /// no tiene esa propiedad: la contraseña solo llega por su camino
    /// explícito, «Ver credenciales»/«Revelar» en la tarjeta de lectura (ver
    /// <see cref="Las_credenciales_no_se_piden_al_abrir_y_la_contrasena_sale_enmascarada_hasta_revelarla"/>)
    /// o «Copiar» en este formulario.
    /// </summary>
    [Fact]
    public async Task Abrir_la_edicion_no_precarga_la_contrasena_y_guardar_sin_tocarla_la_conserva()
    {
        var escena = PrepararEdicion(new MediatorFalso());
        // PrepararEdicion no pone contraseña almacenada: sin una real que
        // pudiera filtrarse, la comprobación de abajo pasaría igual con el
        // defecto sin corregir. Se fija una para que el test sea sensible.
        escena.Mediador.Credenciales[escena.Original.Id] = new("app.dokify.net/acceso", null, "lauburu.prl", "Lauburu.2026", null);
        var cut = await AbrirEdicionAsync(escena);

        escena.Mediador.Enviadas.OfType<ObtenerCredencialAccesoSubcontrataQuery>().Should().BeEmpty(
            "abrir la edición no debe pedir la consulta que descifra la contraseña");
        cut.Markup.Should().NotContain("Lauburu.2026", "la contraseña almacenada no debe llegar nunca al DOM del formulario");
        Control(cut, "Contraseña").GetAttribute("value").Should().BeNullOrEmpty(
            "el campo empieza vacío: la contraseña almacenada nunca llega al formulario");

        await Boton(cut, "Guardar credenciales").ClickAsync(new MouseEventArgs());

        var comando = escena.Mediador.Enviadas.OfType<GuardarCredencialAccesoSubcontrataCommand>().Should().ContainSingle().Subject;
        comando.Contrasena.Should().BeNullOrEmpty(
            "sin tocar el campo, el comando no manda nada — GuardarCredencialAccesoSubcontrataCommandHandler conserva la almacenada (DEC-62)");
    }

    [Fact]
    public async Task Si_guardar_las_credenciales_falla_su_motivo_sale_junto_a_ellas_y_se_puede_reintentar()
    {
        const string motivo = "La URL de acceso es demasiado larga.";
        var escena = PrepararEdicion(new MediatorFalso
        {
            ResultadoGuardarCredenciales = Result.Fallo(Error.Crear("Credencial.UrlDemasiadoLarga", motivo))
        });
        var cut = await AbrirEdicionAsync(escena);
        await Control(cut, "Usuario").InputAsync(new ChangeEventArgs { Value = "lauburu.admin" });

        await Boton(cut, "Guardar credenciales").ClickAsync(new MouseEventArgs());

        cut.FindAll(".alerta-formulario[role=alert]").Select(a => a.TextContent.Trim()).Should().Equal([motivo]);
        Boton(cut, "Guardar credenciales").HasAttribute("disabled").Should().BeFalse("el guardado terminó: el botón no se queda cargando");
        cut.FindAll(".rejilla-info-subcontrata-360").Should().BeEmpty("un rechazo no saca de la edición");
        Toasts.Should().NotContain(m => m.Tono == TonoToast.Exito);

        escena.Mediador.ResultadoGuardarCredenciales = Result.Exito();
        await Boton(cut, "Guardar credenciales").ClickAsync(new MouseEventArgs());

        escena.Mediador.Enviadas.OfType<GuardarCredencialAccesoSubcontrataCommand>().Select(c => c.Usuario)
            .Should().Equal(["lauburu.admin", "lauburu.admin"], "el reintento conserva lo escrito");
        cut.FindAll(".alerta-formulario[role=alert]").Should().BeEmpty("el reintento que sale bien retira el motivo del fallo anterior");
    }

    [Fact]
    public async Task Un_doble_clic_en_Guardar_credenciales_manda_un_solo_comando()
    {
        var guardado = new TaskCompletionSource();
        var escena = PrepararEdicion(new MediatorFalso
        {
            Retener = p => p is GuardarCredencialAccesoSubcontrataCommand ? guardado.Task : null
        });
        var cut = await AbrirEdicionAsync(escena);

        // Sin await: el comando está retenido.
        var primero = Boton(cut, "Guardar credenciales").ClickAsync(new MouseEventArgs());
        var segundo = Boton(cut, "Guardar credenciales").ClickAsync(new MouseEventArgs());

        escena.Mediador.Enviadas.OfType<GuardarCredencialAccesoSubcontrataCommand>().Should().ContainSingle();

        await cut.InvokeAsync(() => guardado.SetResult());
        await primero;
        await segundo;

        escena.Mediador.Enviadas.OfType<GuardarCredencialAccesoSubcontrataCommand>().Should().ContainSingle();
    }

    // ------------------------------------------------ cancelación al retirar el panel

    /// <summary>
    /// El doble convierte la cancelación del token en la de la consulta
    /// retenida, como haría EF: si el panel no cancela, la tarea se queda
    /// pendiente y el token sin cancelar.
    /// </summary>
    [Fact]
    public async Task Retirar_el_panel_cancela_la_consulta_que_estaba_en_vuelo_sin_dejar_una_excepcion_sin_controlar()
    {
        var id = Guid.NewGuid();
        var trabajadores = new TaskCompletionSource();
        var mediador = Registrar(new MediatorFalso
        {
            Retener = p => p is ObtenerTrabajadoresQuery ? trabajadores.Task : null
        });
        mediador.Detalles[id] = Detalle(id, "Pinturas Lauburu S.A.");

        var cut = Renderizar(id);
        var token = mediador.Tokens.Where(t => t.Peticion is ObtenerTrabajadoresQuery)
            .Should().ContainSingle("la cadena está parada en los trabajadores").Subject.Token;
        token.CanBeCanceled.Should().BeTrue("la consulta tiene que llevar el token del panel, no CancellationToken.None");
        token.Register(() => trabajadores.TrySetCanceled(token));

        await DisposeComponentsAsync();

        token.IsCancellationRequested.Should().BeTrue("Dispose cancela lo que ya estaba emitido");
        trabajadores.Task.IsCanceled.Should().BeTrue();
        Renderer.UnhandledException.IsCompleted.Should().BeFalse("la cancelación la absorbe el catch de la propia carga");
        mediador.Enviadas.OfType<ObtenerCentrosConActividadDeSubcontrataQuery>().Should().BeEmpty("y la cadena no sigue");
    }

    [Fact]
    public async Task Todas_las_consultas_de_carga_llevan_el_token_del_panel_y_ninguna_sobrevive_a_retirarlo()
    {
        var escena = PrepararEdicion(new MediatorFalso());
        var id = escena.Original.Id;
        escena.Mediador.Detalles[id] = Detalle(id, "Pinturas Lauburu S.A.", clientes: [escena.Refrielectric], empresas: [escena.Montajes]);
        escena.Mediador.Supervisiones[id] = new SupervisionSubcontrataDto([], [new(Guid.NewGuid(), "Centro Norte", "Refrielectric S.A.")]);
        escena.Mediador.Tipos[AmbitoAplicacion.Empresa] = [TipoCatalogo("Seguro RC", AmbitoAplicacion.Empresa, 1)];

        var cut = Renderizar(id);
        await Boton(cut, "Ver credenciales").ClickAsync(new MouseEventArgs());
        await Boton(cut, "Editar identidad").ClickAsync(new MouseEventArgs());
        cut.Render(p => p.Add(x => x.EntidadId, id).Add(x => x.PestanaActiva, "supervision"));
        await Boton(cut, "+ Registrar verificación").ClickAsync(new MouseEventArgs());

        var consultas = escena.Mediador.Tokens.Where(t => t.Peticion.GetType().Name.EndsWith("Query", StringComparison.Ordinal)).ToList();
        consultas.Select(t => t.Peticion.GetType().Name).Distinct().Should().BeEquivalentTo(
            [
                nameof(ObtenerSubcontrataPorIdQuery), nameof(ObtenerClientesParaSelectorQuery), nameof(ObtenerEmpresasParaSelectorQuery),
                nameof(ObtenerTrabajadoresQuery), nameof(ObtenerCentrosConActividadDeSubcontrataQuery), nameof(ObtenerSupervisionSubcontrataQuery),
                nameof(ObtenerCredencialAccesoSubcontrataQuery), nameof(ObtenerCredencialAccesoSubcontrataSinContrasenaQuery), nameof(ObtenerTiposDocumentoQuery),
            ],
            "el recorrido tiene que haber pasado por todas las consultas que lanza el panel, o la comprobación no las mira");
        consultas.Should().OnlyContain(t => t.Token.CanBeCanceled && !t.Token.IsCancellationRequested);

        await DisposeComponentsAsync();

        consultas.Should().OnlyContain(t => t.Token.IsCancellationRequested, "retirado el panel no queda ninguna consulta suya viva");
    }
}
