using System.Globalization;
using Bunit;
using CaeManager.Application.AsistenteIa.Candidatos;
using CaeManager.Application.AsistenteIa.Ordenes;
using CaeManager.Application.AsistenteIa.Queries.PreguntarAlAsistente;
using CaeManager.Application.AsistenteIa.Queries.ProponerPlan;
using CaeManager.Application.AsistenteIa.Tareas.Commands;
using CaeManager.Application.AsistenteIa.Tareas.Queries;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Domain.AsistenteIa;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.AsistenteIa;
using CaeManager.Infrastructure.Comunicaciones;
using CaeManager.Web.Features.AsistenteIa;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

/// <summary>El Tenant efectivo del circuito, que una prueba puede cambiar en mitad de la conversación (revocación).</summary>
internal sealed class TenantEfectivoDePrueba(Guid? tenantId) : ITenantActual
{
    public Guid? TenantId { get; set; } = tenantId;
}

internal sealed class UsuarioDeVegaDePrueba(Guid? tenantOrigenId = null) : ICurrentUserService
{
    public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(null);
    public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(null);
    public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult<string?>(null);
    public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult(tenantOrigenId);
    public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(false);
}

/// <summary>
/// La interfaz del agente del asistente: propone, no ejecuta. Se prueba con un mediador
/// de guion (no hay red ni proveedor de IA): lo que importa aquí es qué se manda, en qué
/// orden y qué deja confirmar el Enter.
/// </summary>
public class AsistenteIaAgenteTests : BunitContext
{
    private sealed class MediadorDeGuion : IMediator
    {
        public List<object> Recibidos { get; } = [];

        public Dictionary<Type, Func<object, object>> Guion { get; } = [];

        public IEnumerable<T> De<T>() => Recibidos.OfType<T>();

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Recibidos.Add(request);
            if (!Guion.TryGetValue(request.GetType(), out var respuesta))
                throw new InvalidOperationException($"El guion no contempla {request.GetType().Name}.");
            return Task.FromResult((TResponse)respuesta(request));
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

    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
    private static readonly Guid TareaId = Guid.NewGuid();
    private static readonly Guid VersionTarea = Guid.NewGuid();

    private static readonly TenantDeCarteraDto A = new(TenantA, "Hotel Mirador", true);
    private static readonly TenantDeCarteraDto B = new(TenantB, "Industrias Norte", false);

    private readonly MediadorDeGuion _mediador = new();
    private readonly TenantEfectivoDePrueba _tenantEfectivo = new(TenantA);

    public AsistenteIaAgenteTests()
    {
        Services.AddLocalization();
        Services.AddScoped<AsistenteIaService>();
        Services.AddSingleton<IMediator>(_mediador);
        Services.AddSingleton<ISanitizadorHtmlService, GanssSanitizadorHtmlService>();
        Services.AddSingleton<IClienteActivoSeleccionado>(new SeleccionEmpresaGestionadaDePrueba(TenantA));
        Services.AddSingleton<ITenantActual>(_tenantEfectivo);
        Services.AddSingleton<ICurrentUserService>(new UsuarioDeVegaDePrueba(TenantB));
        AddAuthorization().SetAuthorized("laura.perez@consultora.es");
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private void Configurar(bool anthropic, bool typeSafeActivo, bool typeSafeClave = true)
    {
        Services.AddSingleton<IOptions<AnthropicOptions>>(Options.Create(new AnthropicOptions { ApiKey = anthropic ? "sk-ant-de-prueba" : null }));
        Services.AddSingleton<IOptions<TypeSafeOptions>>(Options.Create(
            new TypeSafeOptions { Activo = typeSafeActivo, ApiKey = typeSafeClave ? "ts-de-prueba" : null }));
        Services.AddScoped<DisponibilidadAsistente>();
    }

    private static DatoPropuestoDto Resuelto(string campo, string nombre, Guid tenant) =>
        new(campo, "d", true, Guid.NewGuid(), nombre, tenant, 90, null);

    private static PlanPropuestoDto PlanConfirmable() => new(
        SituacionPlan.Propuesto, CatalogoOrdenesAsistente.ReclamarDocumentacion, 91,
        [Resuelto("destinatario", "Montajes Ebro S.A.", TenantA)],
        new TenantDestinoDto(SituacionTenantDestino.Unico, A, [], "El Tenant abierto."),
        Ejecutable: true, Limitacion: null);

    private static PlanPropuestoDto PlanMezcla() => new(
        SituacionPlan.Propuesto, CatalogoOrdenesAsistente.AltaCentro, 88,
        [Resuelto("cliente_empresarial", "Cliente de A", TenantA)],
        new TenantDestinoDto(SituacionTenantDestino.Mezcla, null, [A, B], "Junta datos de Hotel Mirador e Industrias Norte."),
        Ejecutable: true, Limitacion: null);

    private static TareaAsistenteDto Tarea() => new(
        TareaId, VersionTarea, EstadoTareaAsistente.PlanListo, DateTime.UtcNow, DateTime.UtcNow, null, [], []);

    /// <summary>Guion del camino feliz: la Tarea se crea, el motor propone <paramref name="plan"/> y se guarda.</summary>
    private void GuionDeAgente(PlanPropuestoDto plan)
    {
        _mediador.Guion[typeof(CrearTareaAsistenteCommand)] = _ => Result.Exito(TareaId);
        _mediador.Guion[typeof(ProponerPlanDeTareaAsistenteCommand)] = _ => Result.Exito(plan);
        _mediador.Guion[typeof(ObtenerCandidatosAsistenteQuery)] =
            _ => new CandidatosAsistenteDto([A, B], new Dictionary<string, IReadOnlyList<CandidatoSelladoDto>>());
        _mediador.Guion[typeof(GuardarPlanTareaAsistenteCommand)] = _ => Result.Exito();
        _mediador.Guion[typeof(ObtenerTareaAsistenteQuery)] = _ => Result.Exito(Tarea());
        _mediador.Guion[typeof(ConfirmarPlanTareaAsistenteCommand)] = _ => Result.Exito();
        _mediador.Guion[typeof(DescartarTareaAsistenteCommand)] = _ => Result.Exito();
        GuionDeEmpresa();
    }

    /// <summary>La Empresa gestionada que enseña el selector del menú: la pastilla «Trabajando en».</summary>
    private void GuionDeEmpresa() =>
        _mediador.Guion[typeof(ObtenerClientesAutorizadosQuery)] = _ => (IReadOnlyList<ClienteAutorizadoDto>)
            [new ClienteAutorizadoDto(TenantA, "Hotel Mirador", EsOrigen: false, EsGestionadoPorOperacion: true), new ClienteAutorizadoDto(TenantB, "Industrias Norte", EsOrigen: true)];

    private IRenderedComponent<AsistenteIa> Abrir()
    {
        var panel = Render<AsistenteIa>();
        panel.InvokeAsync(() => Services.GetRequiredService<AsistenteIaService>().Abrir());
        return panel;
    }

    private static void Enviar(IRenderedComponent<AsistenteIa> panel, string texto)
    {
        panel.Find("textarea.asistente-textarea").Input(texto);
        panel.Find("button.asistente-boton-enviar").Click();
    }

    // ---- Disponibilidad: TypeSafe apagado --------------------------------------------------

    [Fact]
    public void Con_TypeSafe_apagado_el_panel_avisa_y_solo_ofrece_las_consultas()
    {
        Configurar(anthropic: true, typeSafeActivo: false);

        var panel = Abrir();

        panel.Find(".asistente-nota").TextContent.Should().Be("Pedir gestiones al asistente no está disponible en este entorno.");
        panel.FindAll(".asistente-modos").Should().BeEmpty("sin agente no hay nada entre lo que elegir");
        panel.FindAll("button[aria-label='Borradores y conversaciones anteriores']").Should().BeEmpty();
        panel.Find("textarea.asistente-textarea").GetAttribute("aria-label").Should().Be("Mensaje para Vega");
    }

    [Fact]
    public void Con_TypeSafe_apagado_una_pregunta_nunca_toca_el_agente()
    {
        Configurar(anthropic: true, typeSafeActivo: false);
        _mediador.Guion[typeof(PreguntarAlAsistenteQuery)] = _ => Result.Exito("Respuesta normativa.");

        var panel = Abrir();
        Enviar(panel, "¿Qué es un plan de seguridad?");

        panel.Find(".asistente-mensaje-markdown").TextContent.Should().Contain("Respuesta normativa.");
        _mediador.Recibidos.Where(r => r is not ObtenerClientesAutorizadosQuery).Should().ContainSingle().Which.Should().BeOfType<PreguntarAlAsistenteQuery>(
            "con TypeSafe apagado no se crea ninguna Tarea ni se pide ningún plan");
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void El_agente_necesita_las_dos_llaves_de_TypeSafe(bool activo, bool clave, bool disponible)
    {
        Configurar(anthropic: false, typeSafeActivo: activo, typeSafeClave: clave);

        var boton = Render<BotonAsistenteIa>();

        (boton.Markup.Length > 0).Should().Be(disponible, "solo Activo y ApiKey a la vez encienden el agente");
    }

    // ---- Plan: mostrar, confirmar, descartar -------------------------------------------------

    [Fact]
    public void Una_orden_se_guarda_antes_de_pedir_el_plan_y_el_plan_se_muestra()
    {
        Configurar(anthropic: true, typeSafeActivo: true);
        GuionDeAgente(PlanConfirmable());

        var panel = Abrir();
        Enviar(panel, "reclama la documentación a Montajes Ebro");

        var orden = _mediador.Recibidos.Select(r => r.GetType()).ToList();
        orden.IndexOf(typeof(CrearTareaAsistenteCommand)).Should().BeLessThan(orden.IndexOf(typeof(ProponerPlanDeTareaAsistenteCommand)),
            "la escritura tiene que estar autorizada antes de que el texto llegue al proveedor");
        _mediador.De<CrearTareaAsistenteCommand>().Single().TextoOriginal.Should().Be("reclama la documentación a Montajes Ebro");

        panel.Find(".plan").GetAttribute("aria-label").Should().Be("Reclamar documentación");
        panel.Find(".plan-chip").GetAttribute("aria-label").Should().Be("Se ejecutará en el Tenant beneficiario Hotel Mirador");
        panel.Find(".plan-dato").TextContent.Should().Contain("Montajes Ebro S.A.");
        panel.Find(".plan-pastilla").TextContent.Should().Be("Listo para confirmar");
        panel.FindAll(".plan-pasos li").Should().HaveCount(2);
        panel.Find(".plan-confirmar").GetAttribute("aria-disabled").Should().Be("false");
    }

    [Fact]
    public void Confirmar_manda_la_version_que_la_persona_ve_y_no_promete_ejecucion()
    {
        Configurar(anthropic: true, typeSafeActivo: true);
        GuionDeAgente(PlanConfirmable());
        var panel = Abrir();
        Enviar(panel, "reclama");

        panel.Find(".plan-confirmar").Click();

        var confirmacion = _mediador.De<ConfirmarPlanTareaAsistenteCommand>().Single();
        confirmacion.TareaId.Should().Be(TareaId);
        confirmacion.VersionVista.Should().Be(VersionTarea);
        panel.Find(".plan-cerrado").TextContent.Should().Contain("no se ha creado ni enviado nada");
    }

    [Fact]
    public void Enter_con_la_caja_vacia_confirma_el_plan_pendiente()
    {
        Configurar(anthropic: true, typeSafeActivo: true);
        GuionDeAgente(PlanConfirmable());
        var panel = Abrir();
        Enviar(panel, "reclama");

        panel.Find("textarea.asistente-textarea").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });

        _mediador.De<ConfirmarPlanTareaAsistenteCommand>().Should().ContainSingle();
    }

    [Fact]
    public void Los_botones_del_plan_detienen_el_teclado_para_que_Enter_sobre_un_chip_no_confirme()
    {
        Configurar(anthropic: true, typeSafeActivo: true);
        GuionDeAgente(PlanConfirmable());
        var panel = Abrir();
        Enviar(panel, "reclama");

        // bUnit no reproduce el burbujeo detenido por :stopPropagation; lo que se puede
        // demostrar aquí es que todos los botones del plan lo declaran, que es lo que
        // impide que el keydown llegue al manejador de Enter del grupo.
        var botones = panel.FindAll(".plan button");
        botones.Should().HaveCountGreaterThan(3);
        botones.Should().OnlyContain(b => b.OuterHtml.Contains("onkeydown:stoppropagation"));
        _mediador.De<ConfirmarPlanTareaAsistenteCommand>().Should().BeEmpty();
    }

    [Fact]
    public void Un_plan_que_mezcla_dos_Tenants_se_ve_bloqueado_y_ni_el_clic_ni_el_Enter_lo_confirman()
    {
        Configurar(anthropic: true, typeSafeActivo: true);
        GuionDeAgente(PlanMezcla());
        var panel = Abrir();
        Enviar(panel, "alta de centro");

        panel.Find(".plan-aviso-peligro").TextContent.Should().Contain("mezcla datos de dos organizaciones");
        panel.Find(".plan-pastilla").TextContent.Should().Be("Bloqueado");
        panel.Find(".plan-confirmar").GetAttribute("aria-disabled").Should().Be("true");

        panel.Find(".plan-confirmar").Click();
        panel.Find(".plan").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
        panel.Find("textarea.asistente-textarea").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });

        _mediador.De<ConfirmarPlanTareaAsistenteCommand>().Should().BeEmpty();
    }

    [Fact]
    public void Un_plan_que_no_se_pudo_guardar_no_se_puede_confirmar()
    {
        Configurar(anthropic: true, typeSafeActivo: true);
        GuionDeAgente(PlanConfirmable());
        _mediador.Guion[typeof(GuardarPlanTareaAsistenteCommand)] = _ => Result.Fallo(Error.Crear("X.Guardar", "No se pudo guardar el plan."));
        var panel = Abrir();
        Enviar(panel, "reclama");

        panel.Find(".plan-aviso-peligro").TextContent.Should().Contain("No se pudo guardar el plan.");
        panel.Find(".plan-confirmar").GetAttribute("aria-disabled").Should().Be("true");
        panel.Find(".plan-confirmar").Click();
        _mediador.De<ConfirmarPlanTareaAsistenteCommand>().Should().BeEmpty();
    }

    [Fact]
    public void Cambiar_el_chip_de_Tenant_vuelve_a_pedir_el_plan_con_ese_Tenant_y_lo_guarda_en_la_misma_Tarea()
    {
        Configurar(anthropic: true, typeSafeActivo: true);
        GuionDeAgente(PlanConfirmable());
        var panel = Abrir();
        Enviar(panel, "reclama");

        panel.FindAll(".plan-tenants .plan-opcion")[1].Click();

        var peticiones = _mediador.De<ProponerPlanDeTareaAsistenteCommand>().ToList();
        peticiones.Should().HaveCount(2);
        peticiones[1].TenantElegido.Should().Be(TenantB);
        _mediador.De<GuardarPlanTareaAsistenteCommand>().Select(g => g.TareaId).Should().OnlyContain(id => id == TareaId).And.HaveCount(2);
        _mediador.De<CrearTareaAsistenteCommand>().Should().ContainSingle();
    }

    [Fact]
    public void Descartar_el_plan_descarta_la_Tarea()
    {
        Configurar(anthropic: true, typeSafeActivo: true);
        GuionDeAgente(PlanConfirmable());
        var panel = Abrir();
        Enviar(panel, "reclama");

        panel.Find(".plan-descartar").Click();

        _mediador.De<DescartarTareaAsistenteCommand>().Should().ContainSingle().Which.TareaId.Should().Be(TareaId);
        panel.Find(".plan-cerrado").TextContent.Should().Contain("No se ha hecho nada");
    }

    // ---- Fallos y control por Tenant ---------------------------------------------------------

    [Fact]
    public void Sin_autorizacion_para_guardar_la_Tarea_el_texto_no_sale_hacia_el_proveedor()
    {
        Configurar(anthropic: true, typeSafeActivo: true);
        GuionDeAgente(PlanConfirmable());
        _mediador.Guion[typeof(CrearTareaAsistenteCommand)] = _ => Result.Fallo<Guid>(Error.Crear("Autorizacion.Denegada", "No tienes permiso."));
        var panel = Abrir();
        Enviar(panel, "reclama");

        panel.Find(".asistente-mensaje-error").TextContent.Should().Be("No tienes permiso.");
        _mediador.De<ProponerPlanDeTareaAsistenteCommand>().Should().BeEmpty();
    }

    [Fact]
    public void Sin_instruccion_de_IA_vigente_se_dice_y_no_queda_ningun_borrador()
    {
        Configurar(anthropic: true, typeSafeActivo: true);
        GuionDeAgente(PlanConfirmable());
        _mediador.Guion[typeof(ProponerPlanDeTareaAsistenteCommand)] = _ => Result.Fallo<PlanPropuestoDto>(
            Error.Crear("AsistenteIa.SinInstruccion", "Esta organización todavía no tiene una instrucción de tratamiento con IA vigente."));
        var panel = Abrir();
        Enviar(panel, "reclama");

        panel.Find(".asistente-mensaje-error").TextContent.Should().Contain("instrucción de tratamiento con IA");
        panel.FindAll(".plan").Should().BeEmpty();
        _mediador.De<DescartarTareaAsistenteCommand>().Should().ContainSingle().Which.TareaId.Should().Be(TareaId);
    }

    [Fact]
    public void Una_orden_no_entendida_avisa_y_descarta_la_Tarea()
    {
        Configurar(anthropic: true, typeSafeActivo: true);
        GuionDeAgente(PlanConfirmable());
        _mediador.Guion[typeof(ProponerPlanDeTareaAsistenteCommand)] =
            _ => Result.Exito(new PlanPropuestoDto(SituacionPlan.NoEntendido, null, 5, [], null, false, null));
        var panel = Abrir();
        Enviar(panel, "qué tiempo hace");

        panel.Find(".asistente-mensaje-aviso[role=status]").TextContent.Should().Be(
            "No tengo una respuesta segura para eso en Hotel Mirador. Prueba a decirlo de otra forma o búscalo con Ctrl+K.");
        panel.FindAll(".plan").Should().BeEmpty();
        _mediador.De<DescartarTareaAsistenteCommand>().Should().ContainSingle();
    }

    // ---- Historial de borradores -------------------------------------------------------------

    [Fact]
    public void El_historial_lista_los_borradores_abiertos_y_permite_descartarlos()
    {
        Configurar(anthropic: true, typeSafeActivo: true);
        GuionDeAgente(PlanConfirmable());
        var borrador = new ResumenTareaAsistenteDto(TareaId, EstadoTareaAsistente.PlanEnBorrador, "alta de un centro", DateTime.UtcNow);
        _mediador.Guion[typeof(ListarTareasAsistenteQuery)] = _ => Result.Exito<IReadOnlyList<ResumenTareaAsistenteDto>>([borrador]);
        var panel = Abrir();

        panel.Find("button[aria-label='Borradores y conversaciones anteriores']").Click();

        panel.Find(".asistente-borrador-texto").TextContent.Should().Be("alta de un centro");
        panel.Find(".asistente-borrador-estado").TextContent.Should().Be("Borrador: falta algo");

        panel.Find(".asistente-borrador-descartar").Click();
        _mediador.De<DescartarTareaAsistenteCommand>().Should().ContainSingle().Which.TareaId.Should().Be(TareaId);
    }

    // ---- Accesibilidad -----------------------------------------------------------------------

    [Fact]
    public void Todos_los_controles_del_panel_con_agente_tienen_nombre_accesible()
    {
        Configurar(anthropic: true, typeSafeActivo: true);
        GuionDeAgente(PlanConfirmable());
        var panel = Abrir();
        Enviar(panel, "reclama");

        panel.Find("textarea.asistente-textarea").GetAttribute("aria-label").Should().Be("Mensaje para Vega");
        foreach (var boton in panel.FindAll("button"))
            (boton.GetAttribute("aria-label") ?? boton.TextContent).Trim().Should().NotBeEmpty($"botón {boton.OuterHtml}");
        panel.FindAll(".plan-tenants[aria-label]").Should().NotBeEmpty();
    }

    // ---- Vega: panel, atajos, modos y contexto -------------------------------------------

    [Fact]
    public void El_panel_es_complementario_y_no_modal()
    {
        Configurar(anthropic: true, typeSafeActivo: true);
        GuionDeEmpresa();

        var panel = Abrir();

        var aside = panel.Find("aside.vega-panel");
        aside.GetAttribute("role").Should().Be("complementary");
        aside.HasAttribute("aria-modal").Should().BeFalse("Vega acompaña a la pantalla, no la bloquea");
        aside.GetAttribute("aria-labelledby").Should().Be("vega-titulo");
        panel.FindAll("[role=dialog]").Should().BeEmpty();
        panel.FindAll(".asistente-superposicion").Should().BeEmpty("sin velo: el contenido sigue usable");
    }

    [Fact]
    public void Ctrl_I_abre_y_pliega_Vega_y_al_plegar_el_foco_vuelve_al_boton()
    {
        Configurar(anthropic: true, typeSafeActivo: true);
        GuionDeEmpresa();
        var panel = Render<AsistenteIa>();
        panel.FindAll("aside.vega-panel").Should().BeEmpty("Vega empieza plegado");

        panel.InvokeAsync(() => panel.Instance.AlternarDesdeJs());
        panel.WaitForAssertion(() => panel.FindAll("aside.vega-panel").Should().ContainSingle());
        JSInterop.VerifyInvoke("marcarAbierto");

        panel.InvokeAsync(() => panel.Instance.AlternarDesdeJs());
        panel.WaitForAssertion(() => panel.FindAll("aside.vega-panel").Should().BeEmpty());
        JSInterop.VerifyInvoke("enfocarBoton");
    }

    [Fact]
    public void Ctrl_I_queda_registrado_como_atajo_global()
    {
        Configurar(anthropic: true, typeSafeActivo: true);

        Render<AsistenteIa>();

        JSInterop.VerifyInvoke("registrarAtajo");
    }

    [Fact]
    public void Esc_dentro_del_panel_pliega_Vega()
    {
        Configurar(anthropic: true, typeSafeActivo: true);
        GuionDeEmpresa();
        var panel = Abrir();

        panel.Find("textarea.asistente-textarea").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });

        panel.WaitForAssertion(() => panel.FindAll("aside.vega-panel").Should().BeEmpty());
        Services.GetRequiredService<AsistenteIaService>().Abierto.Should().BeFalse();
        JSInterop.VerifyInvoke("enfocarBoton");
    }

    [Fact]
    public void En_la_pantalla_completa_Esc_y_Ctrl_I_no_pliegan_nada()
    {
        Configurar(anthropic: true, typeSafeActivo: true);
        GuionDeEmpresa();

        // Vega abierto antes de entrar: en la pantalla completa el panel se ve siempre, así que lo
        // que prueba que Esc y Ctrl+I no pliegan nada es que el estado compartido siga abierto.
        var servicio = Services.GetRequiredService<AsistenteIaService>();
        servicio.Abrir();

        var pantalla = Render<AsistenteIa>(p => p.Add(c => c.PantallaCompleta, true));
        pantalla.Find("textarea.asistente-textarea").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        pantalla.InvokeAsync(() => pantalla.Instance.AlternarDesdeJs());

        servicio.Abierto.Should().BeTrue("ni Esc ni Ctrl+I pliegan Vega en la pantalla completa");
        JSInterop.VerifyNotInvoke("enfocarBoton");

        pantalla.FindAll("aside.vega-panel").Should().ContainSingle();
        pantalla.Find("aside.vega-panel").HasAttribute("data-vega-pantalla").Should().BeTrue();
        pantalla.FindAll(".vega-plegar, .vega-pantalla-completa").Should().BeEmpty();
        pantalla.Find(".vega-pista-campo").TextContent.Should().Be("Enter envía · Mayús+Enter nueva línea");
    }

    [Fact]
    public void Cada_respuesta_lleva_la_etiqueta_de_su_modo_con_su_explicacion()
    {
        Configurar(anthropic: true, typeSafeActivo: true);
        GuionDeAgente(PlanConfirmable());
        _mediador.Guion[typeof(PreguntarAlAsistenteQuery)] = _ => Result.Exito("La formación del art. 19.");
        var panel = Abrir();

        Enviar(panel, "reclama la documentación a Montajes Ebro");
        panel.FindAll(".asistente-modo")[1].Click();
        Enviar(panel, "¿Qué formación hace falta?");

        var modos = panel.FindAll(".vega-a .vega-modo");
        modos.Should().HaveCount(2);
        modos[0].TextContent.Trim().Should().Be("Operativo");
        modos[0].GetAttribute("data-tooltip").Should().Be("Prepara un plan con los pasos. No se ejecuta hasta que pulsas Confirmar.");
        modos[1].TextContent.Trim().Should().Be("Consulta PRL");
        modos[1].GetAttribute("data-tooltip").Should().Be("Responde sobre normativa de prevención. No usa datos de ninguna Empresa.");
        modos[1].GetAttribute("tabindex").Should().Be("0", "el tooltip también se alcanza con el teclado");
    }

    [Fact]
    public void La_pastilla_Trabajando_en_muestra_la_Empresa_gestionada_actual()
    {
        Configurar(anthropic: true, typeSafeActivo: true);
        GuionDeEmpresa();

        var panel = Abrir();

        panel.WaitForAssertion(() => panel.Find(".vega-contexto").TextContent.Should().Contain("Trabajando en"));
        var chip = panel.Find(".vega-chip");
        chip.TextContent.Should().Contain("Hotel Mirador").And.Contain("HM");
        // Veraz en los dos modos: en Operativo el plan puede ir a otra Empresa de la cartera.
        chip.GetAttribute("data-tooltip").Should().StartWith("Empresa gestionada de esta sesión: Hotel Mirador.")
            .And.NotContain("solo usa los datos");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Una_pregunta_rechazada_en_Consulta_no_se_queda_en_el_historial_que_se_reenvia(bool porValidacion)
    {
        Configurar(anthropic: true, typeSafeActivo: false);
        GuionDeEmpresa();
        _mediador.Guion[typeof(PreguntarAlAsistenteQuery)] = _ => porValidacion
            ? throw new FluentValidation.ValidationException(
                [new FluentValidation.Results.ValidationFailure("Historial", "La pregunta no puede superar los 4000 caracteres.")])
            : Result.Fallo<string>(Error.Crear("AsistenteIa.CuotaUsuario", "Has agotado tu cuota de preguntas al asistente."));
        var panel = Abrir();

        Enviar(panel, "Pregunta rechazada");
        panel.Find(".asistente-mensaje-error").TextContent.Should().NotBeEmpty();
        Services.GetRequiredService<AsistenteIaService>().Historial.Should().BeEmpty("la rechazada no es un turno de la conversación");

        _mediador.Guion[typeof(PreguntarAlAsistenteQuery)] = _ => Result.Exito("Respuesta normativa.");
        Enviar(panel, "Pregunta aceptada");

        _mediador.De<PreguntarAlAsistenteQuery>().Last().Historial.Should().ContainSingle()
            .Which.Should().Be(new MensajeChatDto(RolMensajeChat.Usuario, "Pregunta aceptada"));
        Services.GetRequiredService<AsistenteIaService>().Historial.Should().Equal(
            new MensajeChatDto(RolMensajeChat.Usuario, "Pregunta aceptada"),
            new MensajeChatDto(RolMensajeChat.Asistente, "Respuesta normativa."));
    }

    [Theory]
    [InlineData("es-ES", true, "Tu organización en esta sesión: Industrias Norte.")]
    [InlineData("es-ES", false, "Empresa gestionada de esta sesión: Hotel Mirador.")]
    [InlineData("ca-ES", true, "La teva organització en aquesta sessió: Industrias Norte.")]
    [InlineData("ca-ES", false, "Empresa gestionada d'aquesta sessió: Hotel Mirador.")]
    public void Textos_del_contexto_distinguen_el_Tenant_de_origen_del_gestionado(string idioma, bool enOrigen, string inicioTooltip)
    {
        var anterior = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(idioma);
            Configurar(anthropic: true, typeSafeActivo: false);
            GuionDeEmpresa();
            _tenantEfectivo.TenantId = enOrigen ? TenantB : TenantA;

            var panel = Abrir();

            panel.WaitForAssertion(() =>
            {
                var chip = panel.Find(".vega-chip");
                chip.GetAttribute("data-tooltip").Should().StartWith(inicioTooltip);
                chip.GetAttribute("aria-label").Should().Be(chip.GetAttribute("data-tooltip"));
                if (enOrigen)
                    chip.GetAttribute("data-tooltip").Should().NotContain("Empresa gestionada");
            });
        }
        finally
        {
            CultureInfo.CurrentUICulture = anterior;
        }
    }

    [Theory]
    [InlineData("es-ES", true, "Ahora estás trabajando en tu organización")]
    [InlineData("es-ES", false, "La Empresa gestionada ha cambiado")]
    [InlineData("ca-ES", true, "Ara estàs treballant a la teva organització")]
    [InlineData("ca-ES", false, "L'Empresa gestionada ha canviat")]
    public void Aviso_al_cambiar_el_Tenant_efectivo_distingue_el_destino(string idioma, bool volverAlOrigen, string inicioAviso)
    {
        var anterior = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(idioma);
            Configurar(anthropic: true, typeSafeActivo: false);
            GuionDeEmpresa();
            _tenantEfectivo.TenantId = volverAlOrigen ? TenantA : TenantB;
            var panel = Abrir();
            panel.Find("textarea.asistente-textarea").Input("¿Qué formación hace falta?");

            _tenantEfectivo.TenantId = volverAlOrigen ? TenantB : TenantA;
            panel.Find("button.asistente-boton-enviar").Click();

            var aviso = panel.Find(".asistente-mensaje-error").TextContent;
            aviso.Should().StartWith(inicioAviso);
            if (volverAlOrigen)
                aviso.Should().NotContain("Empresa gestionada");
            _mediador.De<PreguntarAlAsistenteQuery>().Should().BeEmpty();
            panel.WaitForAssertion(() => panel.Find(".vega-chip").TextContent.Should()
                .Contain(volverAlOrigen ? "Industrias Norte" : "Hotel Mirador"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = anterior;
        }
    }

    [Fact]
    public void Nueva_conversacion_vacia_el_hilo_y_la_conversacion_sobrevive_a_volver_a_montar_el_panel()
    {
        Configurar(anthropic: true, typeSafeActivo: false);
        GuionDeEmpresa();
        _mediador.Guion[typeof(PreguntarAlAsistenteQuery)] = _ => Result.Exito("Respuesta normativa.");
        var panel = Abrir();
        Enviar(panel, "¿Qué es un plan de seguridad?");
        panel.FindAll(".vega-u").Should().ContainSingle();

        // Otra pantalla del mismo circuito: el hilo vive en AsistenteIaService, no en el componente.
        var otra = Render<AsistenteIa>(p => p.Add(c => c.PantallaCompleta, true));
        otra.WaitForAssertion(() => otra.FindAll(".vega-u").Should().ContainSingle());

        otra.Find(".vega-nueva").Click();
        otra.FindAll(".vega-u").Should().BeEmpty();
        otra.FindAll(".vega-bienvenida").Should().ContainSingle();
    }

    // ---- Tenant efectivo: la conversación no cruza de Tenant -------------------------------

    [Fact]
    public void Si_el_Tenant_efectivo_cambia_en_el_circuito_no_se_reenvia_nada_del_anterior_al_proveedor()
    {
        Configurar(anthropic: true, typeSafeActivo: false);
        GuionDeEmpresa();
        _mediador.Guion[typeof(PreguntarAlAsistenteQuery)] = _ => Result.Exito("Respuesta con datos del primer Tenant.");
        var panel = Abrir();
        Enviar(panel, "¿Qué le falta a la primera?");
        panel.FindAll(".vega-u").Should().ContainSingle();

        // El texto se escribe con el primer Tenant; entre la última tecla y el envío, la
        // revalidación del circuito revoca la delegación y el Tenant efectivo pasa a ser otro
        // sin recargar la página (sin un pintado de por medio: lo que prueba es la guarda del envío).
        panel.Find("textarea.asistente-textarea").Input("¿Y la siguiente?");
        _tenantEfectivo.TenantId = TenantB;
        panel.Find("button.asistente-boton-enviar").Click();

        _mediador.De<PreguntarAlAsistenteQuery>().Should().ContainSingle("con el Tenant cambiado no sale ningún envío");
        panel.FindAll(".vega-u, .vega-a").Should().BeEmpty("el hilo del Tenant anterior ya no se enseña");
        panel.Markup.Should().NotContain("la primera?").And.NotContain("Respuesta con datos del primer Tenant.");
        panel.Find(".asistente-mensaje-error").TextContent.Should().Contain("Ahora estás trabajando en tu organización");
        panel.WaitForAssertion(() => panel.Find(".vega-chip").TextContent.Should().Contain("Industrias Norte"));

        // El texto sigue en la caja; al enviarlo de nuevo, al proveedor solo llega lo del Tenant nuevo.
        panel.Find("button.asistente-boton-enviar").Click();

        _mediador.De<PreguntarAlAsistenteQuery>().Should().HaveCount(2);
        _mediador.De<PreguntarAlAsistenteQuery>().Last().Historial.Should().ContainSingle()
            .Which.Should().Be(new MensajeChatDto(RolMensajeChat.Usuario, "¿Y la siguiente?"));
    }

    [Fact]
    public void Si_el_Tenant_efectivo_cambia_el_plan_anterior_deja_de_pintarse_y_Enter_no_lo_confirma()
    {
        Configurar(anthropic: true, typeSafeActivo: true);
        GuionDeAgente(PlanConfirmable());
        var panel = Abrir();
        Enviar(panel, "reclama la documentación a Montajes Ebro");
        panel.FindAll(".plan").Should().ContainSingle();

        _tenantEfectivo.TenantId = TenantB;
        panel.InvokeAsync(() => Services.GetRequiredService<AsistenteIaService>().Notificar());

        panel.WaitForAssertion(() => panel.FindAll(".plan, .vega-u").Should().BeEmpty());
        panel.Find("textarea.asistente-textarea").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
        _mediador.De<ConfirmarPlanTareaAsistenteCommand>().Should().BeEmpty();
    }

    /// <summary>
    /// La fuga: la lista de borradores vive en el componente, no en la conversación del
    /// circuito, y enseña el texto en claro de cada orden. Leída con un Tenant efectivo, seguía
    /// en pantalla (y se podía descartar) cuando el Tenant efectivo pasaba a ser otro.
    /// </summary>
    [Fact]
    public void Si_el_Tenant_efectivo_cambia_los_borradores_del_anterior_dejan_de_verse()
    {
        Configurar(anthropic: true, typeSafeActivo: true);
        GuionDeAgente(PlanConfirmable());
        var borradorDeA = new ResumenTareaAsistenteDto(TareaId, EstadoTareaAsistente.PlanEnBorrador, "alta del centro de Hotel Mirador", DateTime.UtcNow);
        _mediador.Guion[typeof(ListarTareasAsistenteQuery)] = _ => Result.Exito<IReadOnlyList<ResumenTareaAsistenteDto>>([borradorDeA]);
        var panel = Abrir();
        panel.Find("button[aria-label='Borradores y conversaciones anteriores']").Click();
        panel.Find(".asistente-borrador-texto").TextContent.Should().Be("alta del centro de Hotel Mirador");

        _tenantEfectivo.TenantId = TenantB;
        panel.InvokeAsync(() => Services.GetRequiredService<AsistenteIaService>().Notificar());

        panel.WaitForAssertion(() => panel.Markup.Should().NotContain("alta del centro de Hotel Mirador"));
        panel.FindAll(".asistente-borrador-descartar").Should().BeEmpty();
        _mediador.De<DescartarTareaAsistenteCommand>().Should().BeEmpty();
    }

    /// <summary>Sensibilidad del anterior: con el mismo Tenant efectivo, la lista se sigue viendo tras el mismo aviso.</summary>
    [Fact]
    public void Con_el_mismo_Tenant_efectivo_los_borradores_siguen_a_la_vista()
    {
        Configurar(anthropic: true, typeSafeActivo: true);
        GuionDeAgente(PlanConfirmable());
        var borradorDeA = new ResumenTareaAsistenteDto(TareaId, EstadoTareaAsistente.PlanEnBorrador, "alta del centro de Hotel Mirador", DateTime.UtcNow);
        _mediador.Guion[typeof(ListarTareasAsistenteQuery)] = _ => Result.Exito<IReadOnlyList<ResumenTareaAsistenteDto>>([borradorDeA]);
        var panel = Abrir();
        panel.Find("button[aria-label='Borradores y conversaciones anteriores']").Click();

        panel.InvokeAsync(() => Services.GetRequiredService<AsistenteIaService>().Notificar());

        panel.WaitForAssertion(() => panel.Find(".asistente-borrador-texto").TextContent.Should().Be("alta del centro de Hotel Mirador"));
    }

    /// <summary>
    /// El panel del layout y la pantalla completa comparten la conversación: el aviso de
    /// cambio de Tenant del servicio solo llega a la primera instancia que pregunta. La otra
    /// tiene que darse cuenta por su cuenta de que lo suyo se leyó con otro Tenant.
    /// </summary>
    [Fact]
    public void Si_otra_instancia_se_entera_antes_del_cambio_de_Tenant_el_panel_tampoco_ensena_los_borradores()
    {
        Configurar(anthropic: true, typeSafeActivo: true);
        GuionDeAgente(PlanConfirmable());
        var borradorDeA = new ResumenTareaAsistenteDto(TareaId, EstadoTareaAsistente.PlanEnBorrador, "alta del centro de Hotel Mirador", DateTime.UtcNow);
        _mediador.Guion[typeof(ListarTareasAsistenteQuery)] = _ => Result.Exito<IReadOnlyList<ResumenTareaAsistenteDto>>([borradorDeA]);
        var panel = Abrir();
        panel.Find("button[aria-label='Borradores y conversaciones anteriores']").Click();
        panel.Find(".asistente-borrador-texto").TextContent.Should().Be("alta del centro de Hotel Mirador");

        _tenantEfectivo.TenantId = TenantB;
        var pantalla = Render<AsistenteIa>(p => p.Add(c => c.PantallaCompleta, true));
        pantalla.WaitForAssertion(() => pantalla.Find(".vega-chip").TextContent.Should().Contain("Industrias Norte"));
        panel.InvokeAsync(() => Services.GetRequiredService<AsistenteIaService>().Notificar());

        panel.WaitForAssertion(() => panel.Markup.Should().NotContain("alta del centro de Hotel Mirador"));
    }

    /// <summary>Una lista de borradores que vuelve cuando el Tenant efectivo ya es otro no se pinta.</summary>
    [Fact]
    public void Los_borradores_que_vuelven_con_otro_Tenant_efectivo_no_se_pintan()
    {
        Configurar(anthropic: true, typeSafeActivo: true);
        GuionDeAgente(PlanConfirmable());
        var borradorDeA = new ResumenTareaAsistenteDto(TareaId, EstadoTareaAsistente.PlanEnBorrador, "alta del centro de Hotel Mirador", DateTime.UtcNow);
        _mediador.Guion[typeof(ListarTareasAsistenteQuery)] = _ =>
        {
            // La revalidación retira la Empresa gestionada mientras la lista viaja, y algo repinta Vega.
            _tenantEfectivo.TenantId = TenantB;
            Services.GetRequiredService<AsistenteIaService>().AsegurarTenant(TenantB);
            return Result.Exito<IReadOnlyList<ResumenTareaAsistenteDto>>([borradorDeA]);
        };
        var panel = Abrir();

        panel.Find("button[aria-label='Borradores y conversaciones anteriores']").Click();

        panel.Markup.Should().NotContain("alta del centro de Hotel Mirador");
        panel.FindAll(".asistente-borrador-descartar").Should().BeEmpty();
    }

    /// <summary>Una respuesta de la Consulta PRL que vuelve cuando el Tenant efectivo ya es otro no entra en el hilo nuevo.</summary>
    [Fact]
    public void La_respuesta_que_vuelve_con_otro_Tenant_efectivo_no_entra_en_el_hilo_nuevo()
    {
        Configurar(anthropic: true, typeSafeActivo: false);
        GuionDeEmpresa();
        _mediador.Guion[typeof(PreguntarAlAsistenteQuery)] = _ =>
        {
            _tenantEfectivo.TenantId = TenantB;
            Services.GetRequiredService<AsistenteIaService>().AsegurarTenant(TenantB);
            return Result.Exito("Respuesta con datos del primer Tenant.");
        };
        var panel = Abrir();

        Enviar(panel, "¿Qué le falta a la primera?");

        panel.Markup.Should().NotContain("Respuesta con datos del primer Tenant.");
        Services.GetRequiredService<AsistenteIaService>().Historial.Should().BeEmpty();
    }

    // ---- Teclado, foco y borrador ------------------------------------------------------------

    [Fact]
    public void Sin_ningun_proveedor_configurado_ni_Ctrl_I_ni_el_estado_abren_Vega()
    {
        Configurar(anthropic: false, typeSafeActivo: false);
        var panel = Render<AsistenteIa>();

        panel.InvokeAsync(() => panel.Instance.AlternarDesdeJs());
        Services.GetRequiredService<AsistenteIaService>().Abierto.Should().BeFalse("Ctrl+I no hace nada sin proveedor");

        panel.InvokeAsync(() => Services.GetRequiredService<AsistenteIaService>().Abrir());
        panel.FindAll("aside.vega-panel").Should().BeEmpty();
        JSInterop.VerifyNotInvoke("registrarAtajo");
    }

    [Fact]
    public void Al_reabrir_Vega_el_borrador_que_quedo_se_ve_en_la_caja()
    {
        Configurar(anthropic: true, typeSafeActivo: false);
        GuionDeEmpresa();
        var panel = Abrir();
        panel.Find("textarea.asistente-textarea").Input("borrador a medias");
        var servicio = Services.GetRequiredService<AsistenteIaService>();

        panel.InvokeAsync(servicio.Plegar);
        panel.WaitForAssertion(() => panel.FindAll("aside.vega-panel").Should().BeEmpty());
        panel.InvokeAsync(servicio.Abrir);

        panel.WaitForAssertion(() =>
        {
            var campo = panel.Find("textarea.asistente-textarea");
            (campo.GetAttribute("value") ?? campo.TextContent).Should().Be("borrador a medias",
                "Enter enviaría ese texto: tiene que verse en la caja");
        });
    }

    [Fact]
    public void Esc_con_el_foco_en_Confirmar_tambien_pliega_Vega_y_no_confirma()
    {
        Configurar(anthropic: true, typeSafeActivo: true);
        GuionDeAgente(PlanConfirmable());
        var panel = Abrir();
        Enviar(panel, "reclama la documentación a Montajes Ebro");

        panel.Find(".plan-confirmar").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });

        panel.WaitForAssertion(() => panel.FindAll("aside.vega-panel").Should().BeEmpty());
        _mediador.De<ConfirmarPlanTareaAsistenteCommand>().Should().BeEmpty();
    }

    [Fact]
    public void Un_plan_viejo_no_roba_el_foco_al_volver_a_montar_el_panel()
    {
        Configurar(anthropic: true, typeSafeActivo: true);
        GuionDeAgente(PlanConfirmable());
        var panel = Abrir();
        Enviar(panel, "reclama la documentación a Montajes Ebro");
        JSInterop.VerifyFocusAsyncInvoke(1);

        var servicio = Services.GetRequiredService<AsistenteIaService>();
        panel.InvokeAsync(servicio.Plegar);
        panel.WaitForAssertion(() => panel.FindAll("aside.vega-panel").Should().BeEmpty());
        panel.InvokeAsync(servicio.Abrir);
        panel.WaitForAssertion(() => panel.FindAll(".plan").Should().ContainSingle());

        JSInterop.VerifyFocusAsyncInvoke(1);
    }
}
