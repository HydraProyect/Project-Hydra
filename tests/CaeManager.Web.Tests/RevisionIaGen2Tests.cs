using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Documentos.Commands.AceptarDeteccionesIaEnBloque;
using CaeManager.Application.Documentos.Commands.AplicarDeteccionIaDocumento;
using CaeManager.Application.Documentos.Commands.CorregirRevisionIaDocumento;
using CaeManager.Application.Documentos.Commands.ResolverRevisionIaDocumento;
using CaeManager.Application.Documentos.Queries.ObtenerRevisionesIaPendientes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace CaeManager.Web.Tests;

public partial class RevisionIaGen2Tests : BunitContext
{
    public RevisionIaGen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediadorFalso : IMediator
    {
        public IReadOnlyList<RevisionIaDocumentoDto> Revisiones { get; set; } = [];
        public List<object> Enviadas { get; } = [];
        public List<(object Peticion, CancellationToken Token)> Tokens { get; } = [];
        public Func<object, Task?>? Retener { get; set; }
        public Func<AplicarDeteccionIaDocumentoCommand, Result>? AlAplicar { get; set; }
        public Func<ResolverRevisionIaDocumentoCommand, Result>? AlResolver { get; set; }
        public Func<CorregirRevisionIaDocumentoCommand, Result>? AlCorregir { get; set; }
        public Func<AceptarDeteccionesIaEnBloqueCommand, Result<ResultadoAceptacionEnBloque>>? AlAceptarEnBloque { get; set; }

        public async Task<T> Send<T>(IRequest<T> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            Tokens.Add((request, cancellationToken));
            if (Retener?.Invoke(request) is { } espera)
            {
                await espera;
            }
            object? respuesta = request switch
            {
                ObtenerRevisionesIaPendientesQuery => Revisiones,
                AplicarDeteccionIaDocumentoCommand c => AlAplicar?.Invoke(c) ?? Result.Exito(),
                ResolverRevisionIaDocumentoCommand c => AlResolver?.Invoke(c) ?? Result.Exito(),
                CorregirRevisionIaDocumentoCommand c => AlCorregir?.Invoke(c) ?? Result.Exito(),
                AceptarDeteccionesIaEnBloqueCommand c => AlAceptarEnBloque?.Invoke(c) ?? Result.Exito(new ResultadoAceptacionEnBloque(
                    c.RevisionIds.Select(id => new ResultadoAceptacionRevisionIa(id, true, null, null)).ToList())),
                _ => throw new NotSupportedException(request.GetType().Name)
            };
            return (T)respuesta!;
        }
        public Task Send<T>(T request, CancellationToken cancellationToken = default) where T : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<T> CreateStream<T>(IStreamRequest<T> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<T>(T notification, CancellationToken cancellationToken = default) where T : INotification => Task.CompletedTask;
    }

    private MediadorFalso Registrar(MediadorFalso mediador)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        // AvisoCambiosSinGuardar (P1-E2) saca sus textos de IStringLocalizer<TextosComunes>.
        Services.AddLocalization();
        return mediador;
    }

    private IRenderedComponent<RevisionIaTab> Renderizar(MediadorFalso mediador)
    {
        Registrar(mediador);
        this.ConRolDeEscritura();
        return Render<RevisionIaTab>();
    }

    private static RevisionIaDocumentoDto Revision(string propietario, int confianza, DateOnly? fecha, Guid? trabajadorId = null, bool vigenciaLaFijaElTipo = true) => new(
        Guid.NewGuid(), Guid.NewGuid(), propietario, "Formación PRL", confianza, "Formación PRL", fecha,
        "La lectura requiere revisión.", DateTime.UtcNow, trabajadorId, trabajadorId is null ? Guid.NewGuid() : null,
        VigenciaLaFijaElTipo: vigenciaLaFijaElTipo);

    private static async Task ConfirmarBloqueAsync(IRenderedComponent<RevisionIaTab> cut)
    {
        var dialogo = cut.FindComponents<DialogoConfirmacion>().Single(d => d.Instance.Titulo == "Confirmar las lecturas de confianza alta");
        await cut.InvokeAsync(() => dialogo.Instance.OnConfirmar.InvokeAsync());
    }

    private static IElement Boton(IRenderedComponent<RevisionIaTab> cut, string texto) => cut.FindAll("button")
        .Should().ContainSingle(b => b.TextContent.Trim() == texto, $"debe existir exactamente el botón «{texto}»").Subject;

    private static Task InvocarPrivadoAsync(IRenderedComponent<RevisionIaTab> cut, string nombre, params object?[] argumentos) => cut.InvokeAsync(() =>
        (Task)typeof(RevisionIaTab).GetMethod(nombre, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(cut.Instance, argumentos)!);

    [Theory]
    [InlineData(CaeManager.Infrastructure.Identity.Roles.Administrador, true)]
    [InlineData(CaeManager.Infrastructure.Identity.Roles.GestorCae, false)]
    [InlineData(CaeManager.Infrastructure.Identity.Roles.CoordinadorCae, false)]
    [InlineData(CaeManager.Infrastructure.Identity.Roles.DireccionCae, false)]
    public void Los_enlaces_de_auditoria_y_lectura_ia_de_la_cabecera_son_solo_del_Administrador(string rol, bool visibles)
    {
        Registrar(new MediadorFalso());
        AddAuthorization().SetAuthorized("usuario").SetRoles(rol);

        var cut = Render<CaeManager.Web.Features.Documentos.Pages.RevisionIa>();

        var enlaces = cut.FindAll(".revision-ia-enlaces a").Select(a => a.GetAttribute("href")).ToList();
        if (visibles)
        {
            enlaces.Should().Equal("/auditoria-ia", "/configuracion/lectura-ia");
        }
        else
        {
            enlaces.Should().BeEmpty("las rutas destino exigen Administrador");
        }
    }

    [Fact]
    public async Task Corregir_a_mano_envia_un_solo_command_con_la_fecha_y_cierra_la_revision()
    {
        var revision = Revision("A corregir", 80, new DateOnly(2026, 8, 1)) with
        {
            FechaEmisionIntroducida = new DateOnly(2026, 9, 1)
        };
        var mediador = new MediadorFalso { Revisiones = [revision] };
        var cut = Renderizar(mediador);

        await Boton(cut, "Corregir a mano…").ClickAsync(new MouseEventArgs());
        cut.FindComponents<Drawer>().Should().ContainSingle(d => d.Instance.Visible);
        await Boton(cut, "Guardar y cerrar el aviso").ClickAsync(new MouseEventArgs());

        var enviada = mediador.Enviadas.OfType<CorregirRevisionIaDocumentoCommand>()
            .Should().ContainSingle("control positivo: guardar la corrección alcanza el nuevo command").Subject;
        enviada.RevisionId.Should().Be(revision.Id);
        enviada.FechaEmision.Should().Be(new DateOnly(2026, 9, 1));
        cut.FindComponents<Drawer>().Should().ContainSingle(d => !d.Instance.Visible, "el éxito cierra el drawer");
        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle(m => m.Tono == TonoToast.Exito && m.Mensaje.Contains("corregido"));
        mediador.Enviadas.OfType<ObtenerRevisionesIaPendientesQuery>().Should().HaveCount(2, "el éxito recarga la cola");
    }

    /// <summary>
    /// P1-E2: con la fecha de la corrección manual cambiada, salir se detiene y pregunta;
    /// «Salir y descartar» cierra la corrección. Abrirla sin cambiar la fecha precargada no pregunta.
    /// </summary>
    [Fact]
    public async Task Salir_con_la_correccion_manual_a_medias_pregunta_y_descartar_la_cierra()
    {
        var revision = Revision("A corregir", 80, new DateOnly(2026, 8, 1)) with
        {
            FechaEmisionIntroducida = new DateOnly(2026, 9, 1)
        };
        var cut = Renderizar(new MediadorFalso { Revisiones = [revision] });
        var navegacion = Services.GetRequiredService<NavigationManager>();

        await Boton(cut, "Corregir a mano…").ClickAsync(new MouseEventArgs());
        await cut.InvokeAsync(() => navegacion.NavigateTo("/documentos/revision-ia?sin-cambio"));
        cut.FindAll(".modal-contenido").Should().BeEmpty("la fecha precargada sin tocar no es un cambio");

        var origen = navegacion.Uri;
        await cut.Find(".drawer-panel input[data-campo-fecha]").InputAsync(new ChangeEventArgs { Value = "2026-09-15" });
        await cut.InvokeAsync(() => navegacion.NavigateTo("/documentos/revision-ia?con-cambio"));

        navegacion.Uri.Should().Be(origen, "con la fecha cambiada la navegación se detiene");
        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Salir y descartar").ClickAsync(new MouseEventArgs());

        navegacion.Uri.Should().EndWith("con-cambio");
        cut.FindComponents<Drawer>().Should().OnlyContain(d => !d.Instance.Visible, "confirmar la salida cierra la corrección manual");
    }

    [Fact]
    public async Task Cancelar_correccion_sin_cambios_cierra_y_con_fecha_cambiada_pregunta()
    {
        var revision = Revision("A corregir", 80, new DateOnly(2026, 8, 1)) with
        {
            FechaEmisionIntroducida = new DateOnly(2026, 9, 1)
        };
        var cut = Renderizar(new MediadorFalso { Revisiones = [revision] });

        await Boton(cut, "Corregir a mano…").ClickAsync(new MouseEventArgs());
        await cut.ComprobarQueCancelarSinCambiosCierraAsync(".drawer-pie", ".drawer-panel");
        await Boton(cut, "Corregir a mano…").ClickAsync(new MouseEventArgs());
        await cut.Find(".drawer-panel input[data-campo-fecha]").InputAsync(new ChangeEventArgs { Value = "2026-09-15" });
        await cut.PulsarCancelarDelPieAsync(".drawer-pie");
        await cut.ComprobarQuePreguntaYDescartarAsync(".drawer-panel");
    }

    [Fact]
    public async Task Corregir_a_mano_con_fecha_vacia_muestra_el_error_del_campo_no_envia_y_el_fondo_no_cierra_hasta_corregirla()
    {
        var revision = Revision("A corregir", 80, new DateOnly(2026, 8, 1)) with
        {
            FechaEmisionIntroducida = new DateOnly(2026, 9, 1)
        };
        var mediador = new MediadorFalso { Revisiones = [revision] };
        var cut = Renderizar(mediador);

        await Boton(cut, "Corregir a mano…").ClickAsync(new MouseEventArgs());
        await cut.Find(".drawer-panel input[data-campo-fecha]").InputAsync(new ChangeEventArgs { Value = string.Empty });
        await Boton(cut, "Guardar y cerrar el aviso").ClickAsync(new MouseEventArgs());

        cut.Markup.Should().Contain("Indica una fecha de emisión válida.");
        mediador.Enviadas.OfType<CorregirRevisionIaDocumentoCommand>().Should().BeEmpty("sin fecha válida no se envía nada");
        await cut.Find(".drawer-superposicion").MouseDownAsync(new MouseEventArgs());
        await cut.Find(".drawer-superposicion").ClickAsync(new MouseEventArgs());
        cut.FindComponents<Drawer>().Should().ContainSingle(d => d.Instance.Visible, "con el error del campo visible el clic fuera no cierra");

        await cut.Find(".drawer-panel input[data-campo-fecha]").InputAsync(new ChangeEventArgs { Value = "2026-09-15" });
        cut.Markup.Should().NotContain("Indica una fecha de emisión válida.", "teclear una fecha nueva retira el error");
    }

    [Fact]
    public async Task Corregir_a_mano_fallido_mantiene_el_drawer_abierto_muestra_error_y_no_recarga()
    {
        var revision = Revision("A corregir", 80, new DateOnly(2026, 8, 1)) with
        {
            FechaEmisionIntroducida = new DateOnly(2026, 9, 1)
        };
        var mediador = new MediadorFalso
        {
            Revisiones = [revision],
            AlCorregir = _ => Result.Fallo(Error.Crear("RevisionIa.Fallo", "No se pudo corregir."))
        };
        var cut = Renderizar(mediador);

        await Boton(cut, "Corregir a mano…").ClickAsync(new MouseEventArgs());
        await Boton(cut, "Guardar y cerrar el aviso").ClickAsync(new MouseEventArgs());

        cut.FindComponents<Drawer>().Should().ContainSingle(d => d.Instance.Visible, "un fallo conserva el contexto de corrección");
        cut.Find(".drawer-aviso").TextContent.Should().Contain("No se pudo corregir.", "el fallo se ve también en el aviso del formulario, sin marca de «Hecho» en el botón");
        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle(m => m.Tono == TonoToast.Error && m.Mensaje.Contains("No se pudo corregir."));
        mediador.Enviadas.OfType<ObtenerRevisionesIaPendientesQuery>().Should().ContainSingle("control positivo: el fallo no recarga la cola");
    }

    [Fact]
    public async Task La_guarda_del_panel_bloquea_la_segunda_correccion_manual()
    {
        var inicio = new TaskCompletionSource();
        var espera = new TaskCompletionSource();
        var revision = Revision("Corrección concurrente", 80, new DateOnly(2026, 8, 1)) with
        {
            FechaEmisionIntroducida = new DateOnly(2026, 9, 1)
        };
        var mediador = new MediadorFalso
        {
            Revisiones = [revision],
            Retener = r => r is CorregirRevisionIaDocumentoCommand ? EsperarComandoAsync(inicio, espera) : null
        };
        var cut = Renderizar(mediador);
        await Boton(cut, "Corregir a mano…").ClickAsync(new MouseEventArgs());

        var primera = InvocarPrivadoAsync(cut, "CorregirSeleccionadaAsync");
        await inicio.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var segunda = InvocarPrivadoAsync(cut, "CorregirSeleccionadaAsync");
        mediador.Enviadas.OfType<CorregirRevisionIaDocumentoCommand>().Should().ContainSingle(
            "control positivo: la primera corrección alcanzó el command");

        espera.SetResult();
        await primera.WaitAsync(TimeSpan.FromSeconds(10));
        await segunda.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>La aceptación individual sigue permitida porque el Gestor CAE tiene el motivo delante del botón.</summary>
    [Fact]
    public async Task De_una_en_una_se_acepta_con_el_motivo_a_la_vista_y_sin_el_filtro_de_alertas()
    {
        var conAlerta = Revision("Con aviso", 99, new DateOnly(2026, 9, 1)) with { Motivo = MotivosRevisionIa.NoAptoORestriccion };
        var mediador = new MediadorFalso { Revisiones = [conAlerta] };
        var cut = Renderizar(mediador);

        cut.Find(".revision-ia-motivo").TextContent.Should().Contain(MotivosRevisionIa.NoAptoORestriccion);
        await Boton(cut, "Aceptar detección").ClickAsync(new MouseEventArgs());

        var enviada = mediador.Enviadas.OfType<AplicarDeteccionIaDocumentoCommand>().Should().ContainSingle().Subject;
        enviada.RevisionId.Should().Be(conAlerta.Id);
        enviada.SoloSinAlertaDeterminista.Should().BeFalse();
    }

    [Fact]
    public async Task Si_el_servidor_rechaza_una_alerta_el_motivo_del_rechazo_se_muestra_por_revision()
    {
        var revision = Revision("Rechazada", 99, new DateOnly(2026, 9, 1));
        var mediador = new MediadorFalso
        {
            Revisiones = [revision],
            AlAceptarEnBloque = _ => Result.Exito(new ResultadoAceptacionEnBloque(
            [
                new ResultadoAceptacionRevisionIa(revision.Id, false, CodigosRevisionIa.CodigoAlertaARevisarIndividualmente, "Esta lectura tiene una alerta."),
            ])),
        };
        var cut = Renderizar(mediador);

        await Boton(cut, "Confirmar todos los ≥95 % (1)").ClickAsync(new MouseEventArgs());
        await ConfirmarBloqueAsync(cut);

        cut.Find(".revision-ia-resultado").TextContent.Should().Contain("Rechazada").And.Contain("Esta lectura tiene una alerta.");
    }

    [Fact]
    public async Task El_bloque_incompleto_no_se_anuncia_como_exito_y_lista_lo_que_fallo()
    {
        var primera = Revision("Primera", 96, new DateOnly(2026, 9, 1));
        var segunda = Revision("Segunda", 97, new DateOnly(2026, 9, 2));
        var mediador = new MediadorFalso
        {
            Revisiones = [primera, segunda],
            AlAceptarEnBloque = c => Result.Exito(new ResultadoAceptacionEnBloque(
            [
                new ResultadoAceptacionRevisionIa(primera.Id, true, null, null),
                new ResultadoAceptacionRevisionIa(segunda.Id, false, "RevisionIa.NoEncontrada", "No encontramos esta revisión.")
            ]))
        };
        var cut = Renderizar(mediador);

        await Boton(cut, "Confirmar todos los ≥95 % (2)").ClickAsync(new MouseEventArgs());
        await ConfirmarBloqueAsync(cut);

        var toast = Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle("se observó el desenlace del bloque").Subject;
        toast.Tono.Should().Be(TonoToast.Advertencia);
        toast.Mensaje.Should().Contain("1 de 2");
        var resultado = cut.Find(".revision-ia-resultado").TextContent;
        resultado.Should().Contain("Segunda").And.Contain("No encontramos esta revisión.");
        resultado.Should().NotContain("Primera", "solo se listan las que fallaron");
    }

    [Fact]
    public async Task La_guarda_del_panel_bloquea_la_segunda_confirmacion_de_descartar()
    {
        var espera = new TaskCompletionSource();
        var revision = Revision("A descartar", 80, new DateOnly(2026, 9, 1));
        var mediador = new MediadorFalso
        {
            Revisiones = [revision],
            Retener = r => r is ResolverRevisionIaDocumentoCommand
                ? espera.Task.WaitAsync(TimeSpan.FromSeconds(10))
                : null
        };
        var cut = Renderizar(mediador);

        await Boton(cut, "Descartar la lectura").ClickAsync(new MouseEventArgs());
        var dialogo = cut.FindComponents<DialogoConfirmacion>().Single(d => d.Instance.Titulo == "Descartar la lectura de la IA");
        var primera = cut.InvokeAsync(() => dialogo.Instance.OnConfirmar.InvokeAsync());
        var segunda = cut.InvokeAsync(() => dialogo.Instance.OnConfirmar.InvokeAsync());
        mediador.Enviadas.OfType<ResolverRevisionIaDocumentoCommand>().Should().ContainSingle("control positivo: la primera entrada alcanzó el comando");

        await cut.InvokeAsync(() => espera.SetResult());
        await primera.WaitAsync(TimeSpan.FromSeconds(10));
        await segunda.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task La_guarda_del_panel_bloquea_la_segunda_confirmacion_de_aceptar()
    {
        var inicio = new TaskCompletionSource();
        var espera = new TaskCompletionSource();
        var revision = Revision("A aceptar", 80, new DateOnly(2026, 9, 1));
        var mediador = new MediadorFalso { Revisiones = [revision], Retener = r => r is AplicarDeteccionIaDocumentoCommand ? EsperarComandoAsync(inicio, espera) : null };
        var cut = Renderizar(mediador);

        var primera = InvocarPrivadoAsync(cut, "AceptarDeteccionAsync", revision.Id);
        await inicio.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var segunda = InvocarPrivadoAsync(cut, "AceptarDeteccionAsync", revision.Id);
        mediador.Enviadas.OfType<AplicarDeteccionIaDocumentoCommand>().Should().ContainSingle("control positivo: la primera entrada alcanzó el comando");

        espera.SetResult();
        await primera.WaitAsync(TimeSpan.FromSeconds(10));
        await segunda.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task La_guarda_del_panel_bloquea_la_segunda_confirmacion_del_bloque()
    {
        var inicio = new TaskCompletionSource();
        var espera = new TaskCompletionSource();
        var revision = Revision("En lote", 95, new DateOnly(2026, 9, 1));
        var mediador = new MediadorFalso { Revisiones = [revision], Retener = r => r is AceptarDeteccionesIaEnBloqueCommand ? EsperarComandoAsync(inicio, espera) : null };
        var cut = Renderizar(mediador);

        await Boton(cut, "Confirmar todos los ≥95 % (1)").ClickAsync(new MouseEventArgs());
        var dialogo = cut.FindComponents<DialogoConfirmacion>().Single(d => d.Instance.Titulo == "Confirmar las lecturas de confianza alta");
        var primera = cut.InvokeAsync(() => dialogo.Instance.OnConfirmar.InvokeAsync());
        await inicio.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var segunda = cut.InvokeAsync(() => dialogo.Instance.OnConfirmar.InvokeAsync());
        mediador.Enviadas.OfType<AceptarDeteccionesIaEnBloqueCommand>().Should().ContainSingle("control positivo: el primer bloque alcanzó el comando");

        espera.SetResult();
        await primera.WaitAsync(TimeSpan.FromSeconds(10));
        await segunda.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Cambiar_de_seleccion_no_libera_el_bloqueo_del_comando_en_vuelo()
    {
        var inicio = new TaskCompletionSource();
        var espera = new TaskCompletionSource();
        var primeraRevision = Revision("Primera", 80, new DateOnly(2026, 9, 1));
        var segundaRevision = Revision("Segunda", 80, new DateOnly(2026, 9, 2));
        var mediador = new MediadorFalso
        {
            Revisiones = [primeraRevision, segundaRevision],
            Retener = r => r is AplicarDeteccionIaDocumentoCommand
                ? EsperarComandoAsync(inicio, espera)
                : null
        };
        var cut = Renderizar(mediador);

        var primera = InvocarPrivadoAsync(cut, "AceptarDeteccionAsync", primeraRevision.Id);
        await inicio.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await SeleccionarEnDesplegableAsync(cut, "Segunda");
        var segunda = InvocarPrivadoAsync(cut, "AceptarDeteccionAsync", segundaRevision.Id);
        mediador.Enviadas.OfType<AplicarDeteccionIaDocumentoCommand>().Should().ContainSingle("cambiar de entidad no abre una segunda operación concurrente");

        espera.SetResult();
        await primera.WaitAsync(TimeSpan.FromSeconds(10));
        await segunda.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Cambiar_de_seleccion_no_libera_el_bloqueo_del_bloque_en_vuelo()
    {
        var inicio = new TaskCompletionSource();
        var espera = new TaskCompletionSource();
        var primeraRevision = Revision("Primera", 95, new DateOnly(2026, 9, 1));
        var segundaRevision = Revision("Segunda", 80, new DateOnly(2026, 9, 2));
        var mediador = new MediadorFalso
        {
            Revisiones = [primeraRevision, segundaRevision],
            Retener = r => r is AceptarDeteccionesIaEnBloqueCommand
                ? EsperarComandoAsync(inicio, espera)
                : null
        };
        var cut = Renderizar(mediador);

        await Boton(cut, "Confirmar todos los ≥95 % (1)").ClickAsync(new MouseEventArgs());
        var dialogo = cut.FindComponents<DialogoConfirmacion>().Single(d => d.Instance.Titulo == "Confirmar las lecturas de confianza alta");
        var lote = cut.InvokeAsync(() => dialogo.Instance.OnConfirmar.InvokeAsync());
        await inicio.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await SeleccionarEnDesplegableAsync(cut, "Segunda");
        var aceptar = InvocarPrivadoAsync(cut, "AceptarDeteccionAsync", segundaRevision.Id);
        mediador.Enviadas.OfType<AceptarDeteccionesIaEnBloqueCommand>().Should().ContainSingle("cambiar de entidad no abre una segunda operación mientras el bloque sigue en vuelo");

        espera.SetResult();
        await lote.WaitAsync(TimeSpan.FromSeconds(10));
        await aceptar.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Dispose_durante_aceptar_no_publica_resultado_en_la_interfaz()
    {
        var inicio = new TaskCompletionSource();
        var espera = new TaskCompletionSource();
        var revision = Revision("A aceptar", 80, new DateOnly(2026, 9, 1));
        var mediador = new MediadorFalso { Revisiones = [revision], Retener = r => r is AplicarDeteccionIaDocumentoCommand ? EsperarComandoAsync(inicio, espera) : null };
        var cut = Renderizar(mediador);
        var toast = Services.GetRequiredService<ToastService>();

        var operacion = InvocarPrivadoAsync(cut, "AceptarDeteccionAsync", revision.Id);
        await inicio.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await CierreDePagina.CerrarPaginaAsync(cut, this);
        espera.SetResult();
        await operacion.WaitAsync(TimeSpan.FromSeconds(10));
        toast.Mensajes.Should().BeEmpty("un componente dispuesto no publica el desenlace de aceptar");
    }

    [Fact]
    public async Task Dispose_durante_descartar_no_publica_resultado_en_la_interfaz()
    {
        var inicio = new TaskCompletionSource();
        var espera = new TaskCompletionSource();
        var revision = Revision("A descartar", 80, new DateOnly(2026, 9, 1));
        var mediador = new MediadorFalso { Revisiones = [revision], Retener = r => r is ResolverRevisionIaDocumentoCommand ? EsperarComandoAsync(inicio, espera) : null };
        var cut = Renderizar(mediador);
        var toast = Services.GetRequiredService<ToastService>();

        var operacion = InvocarPrivadoAsync(cut, "ResolverSeleccionadaAsync");
        await inicio.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await CierreDePagina.CerrarPaginaAsync(cut, this);
        espera.SetResult();
        await operacion.WaitAsync(TimeSpan.FromSeconds(10));
        toast.Mensajes.Should().BeEmpty("un componente dispuesto no publica el desenlace de descartar");
    }

    [Fact]
    public async Task Dispose_durante_bloque_no_publica_resultado_en_la_interfaz()
    {
        var inicio = new TaskCompletionSource();
        var espera = new TaskCompletionSource();
        var revision = Revision("En lote", 95, new DateOnly(2026, 9, 1));
        var mediador = new MediadorFalso { Revisiones = [revision], Retener = r => r is AceptarDeteccionesIaEnBloqueCommand ? EsperarComandoAsync(inicio, espera) : null };
        var cut = Renderizar(mediador);
        var toast = Services.GetRequiredService<ToastService>();

        await Boton(cut, "Confirmar todos los ≥95 % (1)").ClickAsync(new MouseEventArgs());
        var dialogo = cut.FindComponents<DialogoConfirmacion>().Single(d => d.Instance.Titulo == "Confirmar las lecturas de confianza alta");
        var operacion = cut.InvokeAsync(() => dialogo.Instance.OnConfirmar.InvokeAsync());
        await inicio.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await CierreDePagina.CerrarPaginaAsync(cut, this);
        espera.SetResult();
        await operacion.WaitAsync(TimeSpan.FromSeconds(10));
        toast.Mensajes.Should().BeEmpty("un componente dispuesto no publica el desenlace del bloque");
    }

    private static Task EsperarComandoAsync(TaskCompletionSource inicio, TaskCompletionSource espera)
    {
        inicio.TrySetResult();
        return espera.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task La_consulta_usa_el_token_del_ciclo_y_dispose_cancela_la_carga_en_vuelo()
    {
        var espera = new TaskCompletionSource();
        var mediador = new MediadorFalso { Retener = r => r is ObtenerRevisionesIaPendientesQuery ? espera.Task.WaitAsync(TimeSpan.FromSeconds(10)) : null };
        Registrar(mediador);
        this.ConRolDeEscritura();
        var cut = Render<RevisionIaTab>();
        var token = mediador.Tokens.Should().ContainSingle("control positivo: se inició la consulta de carga").Subject.Token;

        token.CanBeCanceled.Should().BeTrue();
        await CierreDePagina.CerrarPaginaAsync(cut, this);
        token.IsCancellationRequested.Should().BeTrue("retirar el componente cancela la consulta que permanece en vuelo");
        espera.SetResult();
    }
}
