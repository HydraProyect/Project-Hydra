using Bunit;
using CaeManager.Application.AsistenteIa.Candidatos;
using CaeManager.Application.AsistenteIa.Ordenes;
using CaeManager.Application.AsistenteIa.Queries.PreguntarAlAsistente;
using CaeManager.Application.AsistenteIa.Queries.ProponerPlan;
using CaeManager.Application.AsistenteIa.Tareas.Commands;
using CaeManager.Application.AsistenteIa.Tareas.Queries;
using CaeManager.Application.Common;
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

    public AsistenteIaAgenteTests()
    {
        Services.AddLocalization();
        Services.AddScoped<AsistenteIaService>();
        Services.AddSingleton<IMediator>(_mediador);
        Services.AddSingleton<ISanitizadorHtmlService, GanssSanitizadorHtmlService>();
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
    }

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
        panel.Find("textarea.asistente-textarea").GetAttribute("aria-label").Should().Be("Pregunta al asistente");
    }

    [Fact]
    public void Con_TypeSafe_apagado_una_pregunta_nunca_toca_el_agente()
    {
        Configurar(anthropic: true, typeSafeActivo: false);
        _mediador.Guion[typeof(PreguntarAlAsistenteQuery)] = _ => Result.Exito("Respuesta normativa.");

        var panel = Abrir();
        Enviar(panel, "¿Qué es un plan de seguridad?");

        panel.Find(".asistente-mensaje-markdown").TextContent.Should().Contain("Respuesta normativa.");
        _mediador.Recibidos.Should().ContainSingle().Which.Should().BeOfType<PreguntarAlAsistenteQuery>(
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

        panel.Find(".plan-aviso-peligro").TextContent.Should().Contain("mezcla datos de dos Tenants");
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
            Error.Crear("AsistenteIa.SinInstruccion", "Este tenant todavía no tiene una instrucción de tratamiento con IA vigente."));
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

        panel.Find("[role=status]").TextContent.Should().Contain("No he reconocido una gestión");
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

        panel.Find("textarea.asistente-textarea").GetAttribute("aria-label").Should().Be("Gestión que quieres pedir al asistente");
        foreach (var boton in panel.FindAll("button"))
            (boton.GetAttribute("aria-label") ?? boton.TextContent).Trim().Should().NotBeEmpty($"botón {boton.OuterHtml}");
        panel.FindAll(".plan-tenants[aria-label]").Should().NotBeEmpty();
    }
}
