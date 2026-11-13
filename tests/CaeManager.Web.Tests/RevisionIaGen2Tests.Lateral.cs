using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Documentos.Queries.ObtenerRevisionesIaPendientes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Revisión IA con la revisión en lateral (D-49, artboard «Revision-ia»): selector con filtros, «1 de N ‹ ›», avance
/// automático recordado por usuario, atajos J/K, Consulta sin acciones y estados vacíos.
/// </summary>
public partial class RevisionIaGen2Tests
{
    private static readonly DateOnly Vence = new(2027, 3, 1);

    private static string Selector(IRenderedComponent<RevisionIaTab> cut) => cut.Find(".revision-ia-selector-n").TextContent.Trim();

    private static async Task AbrirDesplegableAsync(IRenderedComponent<RevisionIaTab> cut)
    {
        if (cut.FindAll(".revision-ia-pop").Count == 0)
        {
            await cut.Find(".revision-ia-selector-boton").ClickAsync(new MouseEventArgs());
        }
    }

    private static async Task SeleccionarEnDesplegableAsync(IRenderedComponent<RevisionIaTab> cut, string propietario)
    {
        await AbrirDesplegableAsync(cut);
        var opcion = cut.FindAll("[role=option]")
            .Should().ContainSingle(o => o.TextContent.Contains(propietario), $"la cola ofrece «{propietario}»").Subject;
        await opcion.ClickAsync(new MouseEventArgs());
    }

    private static IElement Navegar(IRenderedComponent<RevisionIaTab> cut, string etiqueta) => cut.Find($"button[aria-label='{etiqueta}']");

    private static MediadorFalso TresQueSeResuelven()
    {
        var mediador = new MediadorFalso
        {
            Revisiones = [Revision("Primera", 50, Vence), Revision("Segunda", 60, Vence), Revision("Tercera", 70, Vence)]
        };
        mediador.AlAplicar = c =>
        {
            mediador.Revisiones = mediador.Revisiones.Where(r => r.Id != c.RevisionId).ToList();
            return Result.Exito();
        };
        return mediador;
    }

    [Fact]
    public async Task Con_el_avance_automatico_activado_se_pasa_solo_a_la_siguiente_con_aviso()
    {
        var mediador = TresQueSeResuelven();
        var cut = Renderizar(mediador);
        cut.Find(".revision-ia-avance input").HasAttribute("checked").Should().BeTrue("D-49: el avance automático viene activado");

        await Boton(cut, "Aceptar detección").ClickAsync(new MouseEventArgs());

        var aviso = Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle().Subject;
        aviso.Mensaje.Should().Be("Primera · Formación PRL revisado. Pasamos a Segunda.");
        cut.Find(".revision-ia-selector-textos").TextContent.Should().Contain("Segunda", "la revisión elegida pasa a ser la siguiente de la cola");
        Selector(cut).Should().Be("1 de 2");
    }

    [Fact]
    public async Task Con_el_avance_automatico_desactivado_no_se_elige_otra_y_se_recuerda_la_preferencia()
    {
        var usuarioId = Guid.NewGuid();
        var modulo = JSInterop.SetupModule("./js/revision-ia.js");
        modulo.Setup<string?>("leer", _ => true).SetResult(null);
        modulo.SetupVoid("guardar", _ => true);
        var mediador = TresQueSeResuelven();
        Registrar(mediador);
        this.ConRolDeEscritura(usuarioId: usuarioId);
        var cut = Render<RevisionIaTab>();

        await cut.Find(".revision-ia-avance input").ChangeAsync(new ChangeEventArgs { Value = false });
        await Boton(cut, "Aceptar detección").ClickAsync(new MouseEventArgs());

        var guardado = modulo.Invocations["guardar"].Should().ContainSingle("la preferencia se guarda al cambiarla").Subject;
        guardado.Arguments.Should().Equal($"revision-ia:avance-automatico:{usuarioId}", "0");
        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle(m => !m.Mensaje.Contains("Pasamos a"));
        cut.FindAll(".revision-ia-cab").Should().BeEmpty("con el avance apagado el lateral pide elegir del desplegable");
        cut.Find(".revision-ia-avance input").HasAttribute("checked").Should().BeFalse();
    }

    [Fact]
    public void La_preferencia_guardada_del_usuario_se_lee_al_abrir_la_pantalla()
    {
        var usuarioId = Guid.NewGuid();
        var modulo = JSInterop.SetupModule("./js/revision-ia.js");
        modulo.Setup<string?>("leer", _ => true).SetResult("0");
        Registrar(TresQueSeResuelven());
        this.ConRolDeEscritura(usuarioId: usuarioId);

        var cut = Render<RevisionIaTab>();

        cut.WaitForAssertion(() => cut.Find(".revision-ia-avance input").HasAttribute("checked").Should().BeFalse("el usuario lo había desactivado"));
        modulo.Invocations["leer"].Should().ContainSingle().Which.Arguments.Should().Equal($"revision-ia:avance-automatico:{usuarioId}");
    }

    /// <summary>
    /// JS de bUnit con la lectura de <c>localStorage</c> en manos del test: el resto de llamadas siguen en el JSInterop suelto.
    /// (Una invocación planificada sin resultado no sirve para esto: bUnit la da por no configurada y la avisa como error.)
    /// </summary>
    private sealed class JsConLecturaTardia(Microsoft.JSInterop.IJSRuntime resto, TaskCompletionSource<string?> lectura) : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => identifier == "import" && args?.FirstOrDefault() as string == "./js/revision-ia.js"
            ? ValueTask.FromResult((TValue)(object)new ModuloConLecturaTardia(lectura))
            : resto.InvokeAsync<TValue>(identifier, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }

    private sealed class ModuloConLecturaTardia(TaskCompletionSource<string?> lectura) : Microsoft.JSInterop.IJSObjectReference
    {
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            identifier == "leer" ? (TValue)(object)(await lectura.Task)! : default!;

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task La_preferencia_guardada_que_llega_tarde_no_pisa_la_casilla_que_el_usuario_ya_cambio()
    {
        var lectura = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Services.AddSingleton<Microsoft.JSInterop.IJSRuntime>(_ => new JsConLecturaTardia(JSInterop.JSRuntime, lectura));
        Registrar(TresQueSeResuelven());
        this.ConRolDeEscritura(usuarioId: Guid.NewGuid());
        var cut = Render<RevisionIaTab>();
        cut.Find(".revision-ia-avance input").HasAttribute("checked").Should().BeTrue("control positivo: el avance viene activado");

        await cut.Find(".revision-ia-avance input").ChangeAsync(new ChangeEventArgs { Value = false });
        lectura.SetResult("1");
        await Task.Delay(100);
        await cut.InvokeAsync(() => { });

        // Se lee el estado del componente, no el DOM: la continuación de la lectura puede no repintar por sí sola.
        typeof(RevisionIaTab).GetField("_avanceAutomatico", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(cut.Instance)
            .Should().Be(false, "el gesto del usuario manda sobre el valor que llegó tarde");
        cut.Find(".revision-ia-avance input").HasAttribute("checked").Should().BeFalse();
    }
    [Fact]
    public async Task La_posicion_y_las_flechas_siguen_a_la_lista_filtrada()
    {
        var sinFecha = Revision("SinFecha", 40, null);
        var mediador = new MediadorFalso { Revisiones = [sinFecha, Revision("Media", 70, Vence), Revision("Alta", 97, Vence)] };
        var cut = Renderizar(mediador);

        Selector(cut).Should().Be("1 de 3");
        Navegar(cut, "Anterior (K)").HasAttribute("disabled").Should().BeTrue("la primera no tiene anterior");
        await Navegar(cut, "Siguiente (J)").ClickAsync(new MouseEventArgs());
        Selector(cut).Should().Be("2 de 3");
        await Navegar(cut, "Siguiente (J)").ClickAsync(new MouseEventArgs());
        Selector(cut).Should().Be("3 de 3");
        Navegar(cut, "Siguiente (J)").HasAttribute("disabled").Should().BeTrue("la última no tiene siguiente");

        await AbrirDesplegableAsync(cut);
        await cut.Find("[data-filtro=sinfecha]").ClickAsync(new MouseEventArgs());

        Selector(cut).Should().Be("1 de 1", "«n de m» cuenta solo lo que deja ver el filtro");
        Navegar(cut, "Anterior (K)").HasAttribute("disabled").Should().BeTrue();
        Navegar(cut, "Siguiente (J)").HasAttribute("disabled").Should().BeTrue();
        await AbrirDesplegableAsync(cut);
        cut.FindAll("[role=option]").Should().ContainSingle(o => o.TextContent.Contains("SinFecha"));
    }

    [Fact]
    public async Task Los_atajos_j_y_k_recorren_la_cola()
    {
        var cut = Renderizar(TresQueSeResuelven());
        var atajos = cut.FindComponent<AtajosListaTeclado>().Instance;

        await cut.InvokeAsync(() => atajos.RecibirAtajo("j"));
        Selector(cut).Should().Be("2 de 3");
        await cut.InvokeAsync(() => atajos.RecibirAtajo("k"));
        Selector(cut).Should().Be("1 de 3");
        await cut.InvokeAsync(() => atajos.RecibirAtajo("x"));
        Selector(cut).Should().Be("1 de 3", "una tecla desconocida no mueve nada");
    }

    [Fact]
    public async Task Elegir_del_desplegable_cambia_el_documento_del_lateral()
    {
        var cut = Renderizar(TresQueSeResuelven());

        await SeleccionarEnDesplegableAsync(cut, "Tercera");

        cut.Find(".revision-ia-selector-textos").TextContent.Should().Contain("Tercera");
        Selector(cut).Should().Be("3 de 3");
        cut.FindAll(".revision-ia-pop").Should().BeEmpty("elegir cierra el desplegable");
    }

    [Fact]
    public void Consulta_ve_la_revision_pero_sin_ninguna_accion_de_escritura()
    {
        Registrar(new MediadorFalso { Revisiones = [Revision("Primera", 97, Vence)] });
        this.ConRolDeEscritura(Roles.Consulta);

        var cut = Render<RevisionIaTab>();

        cut.Find(".revision-ia-selector-textos").TextContent.Should().Contain("Primera");
        var textos = cut.FindAll("button").Select(b => b.TextContent.Trim()).ToList();
        textos.Should().NotContain("Aceptar detección").And.NotContain("Corregir a mano…").And.NotContain("Descartar la lectura");
        textos.Should().NotContain(t => t.StartsWith("Confirmar todos"));
    }

    [Fact]
    public async Task Al_resolver_la_ultima_se_muestra_que_no_quedan_documentos_por_revisar()
    {
        var mediador = new MediadorFalso { Revisiones = [Revision("Única", 80, Vence)] };
        mediador.AlAplicar = c =>
        {
            mediador.Revisiones = [];
            return Result.Exito();
        };
        var cut = Renderizar(mediador);

        await Boton(cut, "Aceptar detección").ClickAsync(new MouseEventArgs());

        cut.Markup.Should().Contain("No quedan documentos por revisar");
        cut.FindAll("a").Should().ContainSingle(a => a.TextContent.Trim() == "Volver a Documentos" && a.GetAttribute("href") == "/documentos" && !a.ClassList.Contains("revision-ia-volver"));
    }

    [Fact]
    public void Sin_pendientes_desde_el_principio_se_dice_sin_revisiones_pendientes()
    {
        var cut = Renderizar(new MediadorFalso());

        cut.Markup.Should().Contain("Sin revisiones pendientes").And.NotContain("No quedan documentos por revisar");
        cut.FindAll(".revision-ia-lateral").Should().BeEmpty();
    }

    private static async Task CambiarSeleccionPorCodigoAsync(IRenderedComponent<RevisionIaTab> cut, Guid revisionId) => await cut.InvokeAsync(() =>
        typeof(RevisionIaTab).GetMethod("SeleccionarRevision", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(cut.Instance, [revisionId]));

    private static List<Guid> IdsDelBloque(MediadorFalso mediador) => mediador.Enviadas
        .OfType<CaeManager.Application.Documentos.Commands.AceptarDeteccionesIaEnBloque.AceptarDeteccionesIaEnBloqueCommand>()
        .Should().ContainSingle("confirmar el bloque despacha un solo Command").Subject.RevisionIds.ToList();

    private static async Task ConfirmarTodosAsync(IRenderedComponent<RevisionIaTab> cut, string rotulo)
    {
        await Boton(cut, rotulo).ClickAsync(new MouseEventArgs());
        await ConfirmarBloqueAsync(cut);
    }

    private static async Task ElegirFiltroAsync(IRenderedComponent<RevisionIaTab> cut, string filtro)
    {
        await AbrirDesplegableAsync(cut);
        await cut.Find($"[data-filtro={filtro}]").ClickAsync(new MouseEventArgs());
        // Cambiar de filtro cambia la revisión elegida y eso cierra el desplegable: se reabre para leer sus opciones.
        await AbrirDesplegableAsync(cut);
    }

    private static IReadOnlyList<string> OpcionesDelDesplegable(IRenderedComponent<RevisionIaTab> cut) =>
        cut.FindAll("[role=option] strong").Select(s => s.TextContent.Trim().Split(' ')[0]).ToList();

    // ---- Qué entra en «Confirmar todos»: EsAceptableEnBloque y el umbral ----

    /// <summary>La confianza del modelo no compensa una alerta determinista: no entra en el bloque, aunque tenga 99 %.</summary>
    [Theory]
    [InlineData(MotivosRevisionIa.NoAptoORestriccion)]
    [InlineData(MotivosRevisionIa.TextoDirigidoAlLector)]
    [InlineData(MotivosRevisionIa.FechaEmisionNoEscrita)]
    [InlineData(MotivosRevisionIa.SinContraste)]
    public async Task Una_revision_con_alerta_no_entra_en_el_bloque_aunque_tenga_confianza_alta(string motivo)
    {
        var conAlerta = Revision("ConAlerta", 99, new DateOnly(2026, 9, 1)) with { Motivo = motivo };
        var aceptable = Revision("Aceptable", 99, new DateOnly(2026, 9, 1));
        var mediador = new MediadorFalso { Revisiones = [conAlerta, aceptable] };
        var cut = Renderizar(mediador);

        await ConfirmarTodosAsync(cut, "Confirmar todos los ≥95 % (1)");

        IdsDelBloque(mediador).Should().Equal(aceptable.Id);
    }

    [Fact]
    public async Task Una_lectura_parcial_no_entra_en_el_bloque_y_su_motivo_queda_a_la_vista()
    {
        var parcial = Revision("Parcial", 99, new DateOnly(2026, 9, 1)) with { Motivo = MotivosRevisionIa.LecturaParcial(26) };
        var aceptable = Revision("Aceptable", 99, new DateOnly(2026, 9, 1));
        var mediador = new MediadorFalso { Revisiones = [parcial, aceptable] };
        var cut = Renderizar(mediador);

        await ConfirmarTodosAsync(cut, "Confirmar todos los ≥95 % (1)");

        IdsDelBloque(mediador).Should().Equal(aceptable.Id);
        cut.Find(".revision-ia-motivo").TextContent.Should().Contain("26 página(s) sin leer");
    }

    [Fact]
    public async Task Si_el_tipo_no_fija_la_vigencia_la_lectura_no_entra_en_el_bloque_aunque_tenga_confianza_alta()
    {
        var aMano = Revision("VigenciaAMano", 99, new DateOnly(2026, 9, 1), vigenciaLaFijaElTipo: false);
        var aceptable = Revision("Aceptable", 99, new DateOnly(2026, 9, 1));
        var mediador = new MediadorFalso { Revisiones = [aMano, aceptable] };
        var cut = Renderizar(mediador);

        await ConfirmarTodosAsync(cut, "Confirmar todos los ≥95 % (1)");

        IdsDelBloque(mediador).Should().Equal(aceptable.Id);
    }

    [Fact]
    public async Task Sin_fecha_de_emision_leida_la_lectura_no_entra_en_el_bloque_aunque_tenga_confianza_alta()
    {
        var sinFecha = Revision("SinFecha", 99, null);
        var aceptable = Revision("Aceptable", 99, new DateOnly(2026, 9, 1));
        var mediador = new MediadorFalso { Revisiones = [sinFecha, aceptable] };
        var cut = Renderizar(mediador);

        await ConfirmarTodosAsync(cut, "Confirmar todos los ≥95 % (1)");

        IdsDelBloque(mediador).Should().Equal(aceptable.Id);
    }

    /// <summary>El 95 exacto entra («≥95 %»); el 94, el 90 y el 80 no.</summary>
    [Fact]
    public async Task El_umbral_del_bloque_es_el_95_exacto_y_no_deja_pasar_confianzas_menores()
    {
        var exacta = Revision("Exacta", 95, new DateOnly(2026, 9, 1));
        var mediador = new MediadorFalso
        {
            Revisiones =
            [
                Revision("Noventa y cuatro", 94, new DateOnly(2026, 9, 1)),
                Revision("Noventa", 90, new DateOnly(2026, 9, 1)),
                Revision("Ochenta", 80, new DateOnly(2026, 9, 1)),
                exacta
            ]
        };
        var cut = Renderizar(mediador);

        await ConfirmarTodosAsync(cut, "Confirmar todos los ≥95 % (1)");

        IdsDelBloque(mediador).Should().Equal(exacta.Id);
    }

    // ---- El máximo del bloque y el rótulo ----

    [Fact]
    public async Task Con_mas_confirmables_que_el_maximo_el_bloque_se_recorta_y_el_rotulo_lo_dice()
    {
        var maximo = CaeManager.Application.Documentos.Commands.AceptarDeteccionesIaEnBloque.AceptarDeteccionesIaEnBloqueCommandHandler.MaximoRevisionesPorBloque;
        var revisiones = Enumerable.Range(0, maximo + 5).Select(i => Revision($"Doc{i}", 96, new DateOnly(2026, 9, 1))).ToList();
        var mediador = new MediadorFalso { Revisiones = revisiones };
        var cut = Renderizar(mediador);

        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().NotContain(t => t.StartsWith("Confirmar todos"),
            "con más del máximo el rótulo no promete «todos»");
        await ConfirmarTodosAsync(cut, $"Confirmar las primeras {maximo} de {maximo + 5} ≥95 %");

        var ids = IdsDelBloque(mediador);
        ids.Should().HaveCount(maximo);
        ids.Should().Equal(revisiones.Take(maximo).Select(r => r.Id), "el recorte conserva el orden de la cola");
    }

    [Fact]
    public async Task Con_exactamente_el_maximo_de_confirmables_el_rotulo_sigue_diciendo_todos()
    {
        var maximo = CaeManager.Application.Documentos.Commands.AceptarDeteccionesIaEnBloque.AceptarDeteccionesIaEnBloqueCommandHandler.MaximoRevisionesPorBloque;
        var revisiones = Enumerable.Range(0, maximo).Select(i => Revision($"Doc{i}", 96, new DateOnly(2026, 9, 1))).ToList();
        var mediador = new MediadorFalso { Revisiones = revisiones };
        var cut = Renderizar(mediador);

        await ConfirmarTodosAsync(cut, $"Confirmar todos los ≥95 % ({maximo})");

        IdsDelBloque(mediador).Should().HaveCount(maximo);
    }

    // ---- Filtros ----

    private static (RevisionIaDocumentoDto Confirmable, RevisionIaDocumentoDto Manual, RevisionIaDocumentoDto ConAlerta, RevisionIaDocumentoDto SinFecha) UnaDeCada() =>
    (
        Revision("Confirmable", 96, Vence),
        Revision("Manual", 80, Vence, vigenciaLaFijaElTipo: false),
        Revision("ConAlerta", 96, Vence) with { Motivo = MotivosRevisionIa.NoAptoORestriccion },
        Revision("SinFecha", 99, null)
    );

    [Fact]
    public async Task El_filtro_Confirmables_deja_solo_las_aceptables_en_bloque()
    {
        var (confirmable, manual, conAlerta, sinFecha) = UnaDeCada();
        var cut = Renderizar(new MediadorFalso { Revisiones = [manual, conAlerta, confirmable, sinFecha] });

        await ElegirFiltroAsync(cut, "confirmables");

        OpcionesDelDesplegable(cut).Should().Equal("Confirmable");
        Selector(cut).Should().Be("1 de 1");
    }

    [Fact]
    public async Task El_filtro_Manual_deja_las_que_tienen_fecha_y_no_son_confirmables()
    {
        var (confirmable, manual, conAlerta, sinFecha) = UnaDeCada();
        var cut = Renderizar(new MediadorFalso { Revisiones = [confirmable, manual, conAlerta, sinFecha] });

        await ElegirFiltroAsync(cut, "manual");

        OpcionesDelDesplegable(cut).Should().BeEquivalentTo(["Manual", "ConAlerta"], "con fecha leída y sin poder entrar en el bloque");
        Selector(cut).Should().Be("1 de 2");
    }

    /// <summary>
    /// «Confirmar todos» es de la cola entera: con «Manual» o «Sin fecha» activos (donde no hay confirmables a la vista) el botón
    /// sigue, cuenta las de toda la cola y solo envía las confirmables.
    /// </summary>
    [Theory]
    [InlineData("manual")]
    [InlineData("sinfecha")]
    public async Task Confirmar_todos_cuenta_las_confirmables_de_toda_la_cola_aunque_el_filtro_no_las_muestre(string filtro)
    {
        var (confirmable, manual, conAlerta, sinFecha) = UnaDeCada();
        var mediador = new MediadorFalso { Revisiones = [manual, confirmable, conAlerta, sinFecha] };
        var cut = Renderizar(mediador);

        await ElegirFiltroAsync(cut, filtro);
        OpcionesDelDesplegable(cut).Should().NotContain("Confirmable", "control positivo: el filtro la oculta");

        await ConfirmarTodosAsync(cut, "Confirmar todos los ≥95 % (1)");

        IdsDelBloque(mediador).Should().Equal(confirmable.Id);
    }

    [Fact]
    public async Task Confirmar_todos_no_cuenta_las_de_confianza_baja_de_la_lista_visible()
    {
        var alta = Revision("Alta", 97, Vence);
        var mediador = new MediadorFalso { Revisiones = [Revision("Media", 70, Vence), alta] };
        var cut = Renderizar(mediador);

        await ConfirmarTodosAsync(cut, "Confirmar todos los ≥95 % (1)");

        IdsDelBloque(mediador).Should().Equal(alta.Id);
    }

    // ---- SiguienteTras: a qué revisión se pasa al terminar una ----

    [Fact]
    public async Task Al_resolver_la_ultima_de_la_lista_se_pasa_a_la_primera_con_aviso()
    {
        var mediador = TresQueSeResuelven();
        var cut = Renderizar(mediador);
        await SeleccionarEnDesplegableAsync(cut, "Tercera");

        await Boton(cut, "Aceptar detección").ClickAsync(new MouseEventArgs());

        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle().Which.Mensaje
            .Should().Be("Tercera · Formación PRL revisado. Pasamos a Primera.");
        cut.Find(".revision-ia-selector-textos").TextContent.Should().Contain("Primera");
        Selector(cut).Should().Be("1 de 2");
    }

    [Fact]
    public async Task Con_un_filtro_activo_el_avance_sigue_la_lista_filtrada_y_no_la_cola()
    {
        var manual1 = Revision("Manual1", 50, Vence, vigenciaLaFijaElTipo: false);
        var normal1 = Revision("Normal1", 97, Vence); // confirmable de verdad (umbral ≥95 %): fuera del filtro Manual
        var manual2 = Revision("Manual2", 70, Vence, vigenciaLaFijaElTipo: false);
        var mediador = new MediadorFalso { Revisiones = [manual1, normal1, manual2] };
        mediador.AlAplicar = c =>
        {
            mediador.Revisiones = mediador.Revisiones.Where(r => r.Id != c.RevisionId).ToList();
            return Result.Exito();
        };
        var cut = Renderizar(mediador);
        await ElegirFiltroAsync(cut, "manual");
        cut.Find(".revision-ia-selector-textos").TextContent.Should().Contain("Manual1");

        await Boton(cut, "Aceptar detección").ClickAsync(new MouseEventArgs());

        cut.Find(".revision-ia-selector-textos").TextContent.Should().Contain("Manual2", "el filtro Manual sigue activo y la siguiente es la de su lista");
        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle().Which.Mensaje.Should().EndWith("Pasamos a Manual2.");
    }

    [Fact]
    public async Task Un_aceptar_fallido_no_avanza_ni_cuenta_la_revision_como_terminada()
    {
        var mediador = TresQueSeResuelven();
        mediador.AlAplicar = _ => Result.Fallo(Error.Crear("RevisionIa.Fallo", "No se pudo aceptar."));
        var cut = Renderizar(mediador);

        await Boton(cut, "Aceptar detección").ClickAsync(new MouseEventArgs());

        cut.Find(".revision-ia-selector-textos").TextContent.Should().Contain("Primera");
        Selector(cut).Should().Be("1 de 3");
        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle(m => m.Tono == TonoToast.Error, "solo el aviso del error: ningún «Pasamos a»");
        mediador.Enviadas.OfType<CaeManager.Application.Documentos.Queries.ObtenerRevisionesIaPendientes.ObtenerRevisionesIaPendientesQuery>()
            .Should().ContainSingle("un fallo no recarga la cola");
    }

    [Fact]
    public async Task Un_descartar_fallido_no_avanza_ni_cuenta_la_revision_como_terminada()
    {
        var mediador = TresQueSeResuelven();
        mediador.AlResolver = _ => Result.Fallo(Error.Crear("RevisionIa.Fallo", "No se pudo descartar."));
        var cut = Renderizar(mediador);

        await InvocarPrivadoAsync(cut, "ResolverSeleccionadaAsync");

        cut.Find(".revision-ia-selector-textos").TextContent.Should().Contain("Primera");
        Selector(cut).Should().Be("1 de 3");
        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle(m => m.Tono == TonoToast.Error);
        mediador.Enviadas.OfType<CaeManager.Application.Documentos.Queries.ObtenerRevisionesIaPendientes.ObtenerRevisionesIaPendientesQuery>()
            .Should().ContainSingle("un fallo no recarga la cola");
    }

    // ---- Desplegable: guarda de operación en curso y accesibilidad ----

    [Fact]
    public async Task Con_un_command_en_vuelo_elegir_otra_opcion_del_desplegable_no_cambia_la_seleccion()
    {
        var inicio = new TaskCompletionSource();
        var espera = new TaskCompletionSource();
        var mediador = TresQueSeResuelven();
        mediador.Retener = r => r is CaeManager.Application.Documentos.Commands.AplicarDeteccionIaDocumento.AplicarDeteccionIaDocumentoCommand
            ? EsperarComandoAsync(inicio, espera)
            : null;
        var cut = Renderizar(mediador);

        var aceptar = Boton(cut, "Aceptar detección").ClickAsync(new MouseEventArgs());
        await inicio.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await SeleccionarEnDesplegableAsync(cut, "Tercera");

        cut.Find(".revision-ia-selector-textos").TextContent.Should().Contain("Primera", "la selección no cambia mientras hay un Command en vuelo");
        Selector(cut).Should().Be("1 de 3");

        espera.SetResult();
        await aceptar.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task El_listbox_solo_contiene_opciones_y_los_filtros_y_el_pie_quedan_fuera()
    {
        var cut = Renderizar(TresQueSeResuelven());
        await AbrirDesplegableAsync(cut);

        var listbox = cut.Find("[role=listbox]");
        listbox.Children.Should().OnlyContain(h => h.GetAttribute("role") == "option", "un listbox contiene solo opciones");
        listbox.Children.Should().HaveCount(3);
        listbox.QuerySelector(".revision-ia-filtros").Should().BeNull();
        listbox.QuerySelector(".revision-ia-pop-pie").Should().BeNull();
        cut.Find(".revision-ia-pop").QuerySelector(".revision-ia-filtros").Should().NotBeNull("los filtros siguen en el panel");
        cut.Find(".revision-ia-selector-boton").HasAttribute("aria-haspopup").Should().BeFalse("el panel no es solo un listbox");
    }

    [Fact]
    public async Task Al_abrir_el_desplegable_se_instala_el_teclado_de_las_opciones_una_sola_vez()
    {
        var modulo = JSInterop.SetupModule("./js/revision-ia.js");
        modulo.SetupVoid("instalarTecladoOpciones", _ => true);
        var cut = Renderizar(TresQueSeResuelven());

        await AbrirDesplegableAsync(cut);
        await cut.Find("[data-filtro=todas]").ClickAsync(new MouseEventArgs());

        modulo.Invocations["instalarTecladoOpciones"].Should().ContainSingle("las flechas, Inicio y Fin se instalan al abrir, no en cada repintado");
    }

    [Fact]
    public async Task Escape_cierra_el_desplegable_y_devuelve_el_foco_al_disparador()
    {
        var cut = Renderizar(TresQueSeResuelven());
        await AbrirDesplegableAsync(cut);
        var antes = JSInterop.Invocations.Count(i => i.Identifier.EndsWith("domWrapper.focus"));

        await cut.Find(".revision-ia-lateral").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });

        cut.FindAll(".revision-ia-pop").Should().BeEmpty("Escape cierra el desplegable");
        JSInterop.Invocations.Count(i => i.Identifier.EndsWith("domWrapper.focus")).Should().Be(antes + 1, "el foco vuelve al disparador");
    }

    [Fact]
    public async Task Escape_con_el_desplegable_cerrado_no_mueve_el_foco()
    {
        var cut = Renderizar(TresQueSeResuelven());
        var antes = JSInterop.Invocations.Count(i => i.Identifier.EndsWith("domWrapper.focus"));

        await cut.Find(".revision-ia-lateral").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });

        JSInterop.Invocations.Count(i => i.Identifier.EndsWith("domWrapper.focus")).Should().Be(antes);
    }
}