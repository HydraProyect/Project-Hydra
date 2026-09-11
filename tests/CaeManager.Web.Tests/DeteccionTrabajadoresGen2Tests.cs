using System.Reflection;
using System.Security.Claims;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
using CaeManager.Application.Trabajadores.Commands.ResolverDeteccionAusente;
using CaeManager.Application.Trabajadores.Commands.ResolverDeteccionNuevo;
using CaeManager.Application.Trabajadores.Queries.ObtenerDeteccionesPorEmpresa;
using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Empresas.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Detección de personal contra su mockup Gen 2 («Deteccion Trabajadores
/// TALVEG.dc.html»). La confirmación de la baja la sigue probando
/// <see cref="ConfirmacionAccionesDestructivasTests"/>; esto cubre lo que el
/// rediseño añadió o corrigió.
///
/// <para>
/// <b>Lo que esto SÍ observa:</b> qué consultas y comandos llegan al mediador,
/// con qué parámetros y cuántas veces; qué se pinta con lo que vuelve; y qué
/// pasa cuando las respuestas llegan fuera de orden o después de retirar la
/// página (<see cref="TaskCompletionSource{TResult}"/>). El doble responde
/// según sus parámetros, como los handlers reales: la lista, por empresa y
/// solo con las detecciones sin resolver, en el orden de
/// <c>ObtenerDeteccionesPorEmpresaQueryHandler</c>; las resoluciones, con los
/// mismos desenlaces que <c>ResolverDeteccionNuevoCommandHandler</c> y
/// <c>ResolverDeteccionAusenteCommandHandler</c> (no encontrada, ya resuelta,
/// DNI duplicado, baja, ya no estaba activo), y lo resuelto deja de salir en
/// la siguiente consulta.
/// </para>
///
/// <para>
/// <b>Lo que NO observa:</b> el aspecto (bUnit no evalúa CSS), la autorización
/// real de la ruta y de los comandos, ni que los handlers reales hagan lo que
/// el doble imita — eso se prueba en Application e integración. La columna
/// «Comprobación» sí usa la regla real: la página llama a
/// <see cref="ValidadorIdentificacion"/>.
/// </para>
/// </summary>
public class DeteccionTrabajadoresGen2Tests : BunitContext
{
    /// <summary>Modal, TextoFechaCopiable, BotonCopiar y AtajosListaTeclado importan módulos JS: quedan fuera de lo que se observa aquí.</summary>
    public DeteccionTrabajadoresGen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly Guid EmpresaA = Guid.Parse("e1e1e1e1-0000-0000-0000-000000000001");
    private static readonly Guid EmpresaB = Guid.Parse("e2e2e2e2-0000-0000-0000-000000000002");

    // ---------------------------------------------------------------- dobles

    private sealed class MediadorControlado(Func<object, CancellationToken, Task<object?>> responder) : IMediator
    {
        public List<(object Peticion, CancellationToken Token)> Recibidas { get; } = [];

        public IEnumerable<object> Enviados => Recibidas.Select(r => r.Peticion);

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Recibidas.Add((request, cancellationToken));
            return (TResponse)(await responder(request, cancellationToken))!;
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest
        {
            Recibidas.Add((request!, cancellationToken));
            return Task.CompletedTask;
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        {
            Recibidas.Add((request, cancellationToken));
            return Task.FromResult<object?>(null);
        }

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    /// <summary>Una detección persistida: a qué empresa pertenece, si ya se resolvió y cómo.</summary>
    private sealed class Registro(Guid empresaId, DeteccionTrabajadorDto dto)
    {
        public Guid EmpresaId { get; } = empresaId;
        public DeteccionTrabajadorDto Dto { get; } = dto;
        public string? AccionTomada { get; set; }
        public bool Resuelta => AccionTomada is not null;
    }

    /// <summary>El «servidor» del test: lo que un comando que el test deja pasar cambia, la siguiente consulta lo devuelve.</summary>
    private sealed class Escenario
    {
        public Dictionary<Guid, string> Empresas { get; } = new()
        {
            [EmpresaA] = "Montajes Ebro S.A.",
            [EmpresaB] = "Frío Industrial Aragón S.L."
        };

        public List<Registro> Detecciones { get; } = [];

        /// <summary>DNI que ya tiene un trabajador en el tenant (el alta falla con DniDuplicado, como en el handler).</summary>
        public HashSet<string> DnisExistentes { get; } = [];

        /// <summary>Trabajadores activos: los que una baja puede dar de baja.</summary>
        public HashSet<Guid> TrabajadoresActivos { get; } = [];

        public List<(Guid EmpresaId, string Nombre, string Dni)> TrabajadoresCreados { get; } = [];

        /// <summary>
        /// Si devuelve una tarea, ESA es la respuesta: sustituye a la del «servidor»
        /// (un fallo, una lista vieja). Recibe el token con el que la página envió
        /// la petición, para poder imitar a un handler que lo honra.
        /// </summary>
        public Func<object, CancellationToken, Task<object?>?> Interceptar { get; set; } = (_, _) => null;

        /// <summary>
        /// Si devuelve una tarea, la petición espera a que el test la complete y
        /// después se responde como siempre: un comando retenido aplica su efecto
        /// al soltarse, igual que el handler real cuando por fin responde.
        /// </summary>
        public Func<object, Task?> Retener { get; set; } = _ => null;

        public DeteccionTrabajadorDto Nuevo(Guid empresaId, string nombre, string apellidos, string dni, DateTime? creadaEnUtc = null) =>
            Agregar(empresaId, new DeteccionTrabajadorDto(Guid.NewGuid(), TipoDeteccion.Nuevo, nombre, apellidos, dni, null, creadaEnUtc ?? Ahora));

        public DeteccionTrabajadorDto Ausente(Guid empresaId, string nombre, string apellidos, string dni, DateTime? creadaEnUtc = null)
        {
            var trabajadorId = Guid.NewGuid();
            TrabajadoresActivos.Add(trabajadorId);
            return Agregar(empresaId, new DeteccionTrabajadorDto(Guid.NewGuid(), TipoDeteccion.Ausente, nombre, apellidos, dni, trabajadorId, creadaEnUtc ?? Ahora));
        }

        private DeteccionTrabajadorDto Agregar(Guid empresaId, DeteccionTrabajadorDto dto)
        {
            Detecciones.Add(new Registro(empresaId, dto));
            return dto;
        }

        public Registro RegistroDe(DeteccionTrabajadorDto dto) => Detecciones.Single(r => r.Dto.Id == dto.Id);

        public async Task<object?> Responder(object peticion, CancellationToken token)
        {
            if (Interceptar(peticion, token) is { } respuesta)
                return await respuesta;

            if (Retener(peticion) is { } puerta)
                await puerta;

            return ResponderAhora(peticion);
        }

        private object? ResponderAhora(object peticion) =>
            peticion switch
            {
                ObtenerEmpresaPorIdQuery q => Empresas.TryGetValue(q.Id, out var razonSocial)
                    ? new EmpresaDetalleDto(q.Id, razonSocial, null, DateTime.UtcNow, [], Guid.NewGuid())
                    : null,
                ObtenerDeteccionesPorEmpresaQuery q => Lista(q.EmpresaId),
                ResolverDeteccionNuevoCommand c => ResolverNuevo(c),
                ResolverDeteccionAusenteCommand c => ResolverAusente(c),
                _ => throw new NotSupportedException($"Petición no prevista en este test: {peticion.GetType().Name}.")
            };

        /// <summary>Como el handler: las de esa empresa, sin resolver, por tipo y apellidos.</summary>
        public Result<IReadOnlyList<DeteccionTrabajadorDto>> Lista(Guid empresaId) =>
            Result.Exito<IReadOnlyList<DeteccionTrabajadorDto>>(Detecciones
                .Where(r => r.EmpresaId == empresaId && !r.Resuelta)
                .Select(r => r.Dto)
                .OrderBy(d => d.Tipo).ThenBy(d => d.Apellidos, StringComparer.Ordinal)
                .ToList());

        private Result ResolverNuevo(ResolverDeteccionNuevoCommand c)
        {
            var registro = Detecciones.SingleOrDefault(r => r.Dto.Id == c.DeteccionId && r.Dto.Tipo == TipoDeteccion.Nuevo);
            if (registro is null)
                return Result.Fallo(Error.Crear("Deteccion.NoEncontrada", "No encontramos esta detección."));
            if (registro.Resuelta)
                return Result.Fallo(Error.Crear("Deteccion.YaResuelta", "Esta detección ya fue gestionada."));

            if (!c.Crear)
            {
                registro.AccionTomada = "Descartado";
                return Result.Exito();
            }

            if (DnisExistentes.Contains(registro.Dto.Dni))
                return Result.Fallo(Error.Crear("Deteccion.DniDuplicado", "Ya existe un trabajador con este DNI — revisa el listado de trabajadores."));

            TrabajadoresCreados.Add((registro.EmpresaId, $"{registro.Dto.Nombre} {registro.Dto.Apellidos}", registro.Dto.Dni));
            registro.AccionTomada = "Creado";
            return Result.Exito();
        }

        private Result<ResultadoResolucionAusente> ResolverAusente(ResolverDeteccionAusenteCommand c)
        {
            var registro = Detecciones.SingleOrDefault(r => r.Dto.Id == c.DeteccionId && r.Dto.Tipo == TipoDeteccion.Ausente);
            if (registro is null)
                return Result.Fallo<ResultadoResolucionAusente>(Error.Crear("Deteccion.NoEncontrada", "No encontramos esta detección."));
            if (registro.Resuelta)
                return Result.Fallo<ResultadoResolucionAusente>(Error.Crear("Deteccion.YaResuelta", "Esta detección ya fue gestionada."));

            if (!c.Desactivar)
            {
                registro.AccionTomada = "Mantenido";
                return Result.Exito(ResultadoResolucionAusente.Mantenido);
            }

            if (!TrabajadoresActivos.Remove(registro.Dto.TrabajadorExistenteId!.Value))
            {
                registro.AccionTomada = "YaNoActivo";
                return Result.Exito(ResultadoResolucionAusente.YaNoEstabaActivo);
            }

            registro.AccionTomada = "Desactivado";
            return Result.Exito(ResultadoResolucionAusente.DadoDeBaja);
        }
    }

    private static readonly DateTime Ahora = new(2026, 9, 5, 5, 12, 0, DateTimeKind.Utc);

    /// <summary>
    /// Evalúa los roles de verdad contra el principal —mismo arnés que
    /// <c>ClientesVacioPorFiltroTests</c>—: uno que autorizara siempre dejaría
    /// ver al Gestor CAE lo que la página reserva al Administrador.
    /// </summary>
    private sealed class AutorizacionPorRoles : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements)
        {
            var cumple = requirements.All(r => r switch
            {
                RolesAuthorizationRequirement roles => roles.AllowedRoles.Any(user.IsInRole),
                DenyAnonymousAuthorizationRequirement => user.Identity?.IsAuthenticated == true,
                _ => true
            });

            return Task.FromResult(cumple ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        }

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
            Task.FromResult(AuthorizationResult.Success());
    }

    private sealed class AutenticacionFalsa(string rol) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Role, rol)], "test"))));
    }

    // ---------------------------------------------------------------- arnés

    private (IRenderedComponent<DeteccionTrabajadores> Cut, MediadorControlado Mediador) Renderizar(
        Escenario escenario, Guid? empresaId = null, string rol = Roles.GestorCae)
    {
        var mediador = new MediadorControlado(escenario.Responder);
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<AuthenticationStateProvider>(_ => new AutenticacionFalsa(rol));
        Services.AddAuthorizationCore();
        Services.AddScoped<IAuthorizationService, AutorizacionPorRoles>();
        Services.AddCascadingAuthenticationState();

        var cut = Render<DeteccionTrabajadores>(p => p.Add(c => c.EmpresaId, empresaId ?? EmpresaA));
        return (cut, mediador);
    }

    private ToastMensaje UltimoToast() => Services.GetRequiredService<ToastService>().Mensajes.Last();

    private static string Texto(IElement elemento) => elemento.TextContent.Trim();

    /// <summary>Estado privado de una instancia ya retirada: bUnit no deja leer <c>cut.Instance</c> tras desecharla.</summary>
    private static T Campo<T>(DeteccionTrabajadores instancia, string nombre) =>
        (T)(typeof(DeteccionTrabajadores).GetField(nombre, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"DeteccionTrabajadores ya no tiene el campo {nombre}: el test ha dejado de observar lo que dice."))
        .GetValue(instancia)!;

    private static IElement FilaDe(IRenderedComponent<DeteccionTrabajadores> cut, DeteccionTrabajadorDto deteccion) =>
        cut.Find($"tr[data-deteccion='{deteccion.Id}']");

    private static IElement BotonDeFila(IRenderedComponent<DeteccionTrabajadores> cut, DeteccionTrabajadorDto deteccion, string texto) =>
        FilaDe(cut, deteccion).QuerySelectorAll("button").Single(b => Texto(b) == texto);

    private static Task Pulsar(IRenderedComponent<DeteccionTrabajadores> cut, DeteccionTrabajadorDto deteccion, string texto) =>
        BotonDeFila(cut, deteccion, texto).ClickAsync(new MouseEventArgs());

    private static IElement BotonDelDialogo(IRenderedComponent<DeteccionTrabajadores> cut, string texto) =>
        cut.FindAll(".modal-pie button").Single(b => Texto(b) == texto);

    private static IEnumerable<string> NombresPintados(IRenderedComponent<DeteccionTrabajadores> cut) =>
        cut.FindAll("td.deteccion-nombre").Select(Texto);

    private static IEnumerable<object> Comandos(MediadorControlado mediador) =>
        mediador.Enviados.Where(p => p is ResolverDeteccionNuevoCommand or ResolverDeteccionAusenteCommand);

    // ---------------------------------------------------------------- lo que se pinta

    [Fact]
    public void Nuevos_y_ausentes_salen_en_su_seccion_con_su_cuenta_y_la_comprobacion_real_del_dni()
    {
        var escenario = new Escenario();
        escenario.Nuevo(EmpresaA, "Iker", "Mena Ruiz", "12345678Z");
        escenario.Nuevo(EmpresaA, "Sofía", "Landa Vera", "12345678A");
        escenario.Nuevo(EmpresaA, "Lucía", "Nogal Pérez", "X1234567L");
        escenario.Nuevo(EmpresaA, "Jon", "Olano Sáez", "12345678-Z");
        escenario.Nuevo(EmpresaA, "Amir", "Qasim", "PAB123456");
        escenario.Ausente(EmpresaA, "Nuria", "Salas Prieto", "11223344B");
        var (cut, _) = Renderizar(escenario);

        cut.Find("#deteccion-nuevos-titulo").TextContent.Trim().Should().Be("Trabajadores nuevos detectados (5)");
        cut.Find("#deteccion-ausentes-titulo").TextContent.Trim().Should().Be("Trabajadores ausentes (1)");

        string Comprobacion(string nombre) =>
            Texto(cut.FindAll("tbody tr").Single(f => Texto(f.QuerySelector("td.deteccion-nombre")!) == nombre)
                .QuerySelector("td.deteccion-comprobacion")!);

        Comprobacion("Iker Mena Ruiz").Should().Be("Dígito de control correcto");
        Comprobacion("Sofía Landa Vera").Should().Be("Dígito de control incorrecto", "la letra de 12345678 es la Z");
        Comprobacion("Lucía Nogal Pérez").Should().Be("Dígito de control correcto", "NIE: la X vale 0");
        Comprobacion("Jon Olano Sáez").Should().Be("Dígito de control correcto",
            "el servicio quita guiones y espacios antes de comprobar el dígito de control: la pantalla no puede decir lo contrario de lo que decidió");
        Comprobacion("Amir Qasim").Should().Be("No es DNI ni NIE: sin comprobar");

        NombresPintados(cut).Should().Equal(
            ["Sofía Landa Vera", "Iker Mena Ruiz", "Lucía Nogal Pérez", "Jon Olano Sáez", "Amir Qasim", "Nuria Salas Prieto"],
            "primero los nuevos y después los ausentes, cada grupo por apellidos como los ordena el handler");
    }

    /// <summary>
    /// La IA propone y una persona decide; y la pantalla no sabe ni el
    /// documento de cada fila ni cuántas personas leyó. Nada puede afirmarlo.
    /// </summary>
    [Fact]
    public void Ningun_texto_atribuye_a_la_IA_una_decision_ni_inventa_datos_que_la_pantalla_no_tiene()
    {
        var escenario = new Escenario();
        escenario.Nuevo(EmpresaA, "Iker", "Mena Ruiz", "12345678Z");
        escenario.Ausente(EmpresaA, "Nuria", "Salas Prieto", "11223344B");
        var (cut, _) = Renderizar(escenario);

        cut.Markup.Should().Contain("nada se hace", "la barrera: el texto de la cabecera está pintado");
        cut.Markup.Should().NotContainAny(
            ["confianza", "precisión", "%", "Reversible", "RNT de agosto", "Personas leídas", "tope de seguridad", "no aparecerá en futuras detecciones"]);
        cut.Find(".deteccion-panel-nota").TextContent.Trim()
            .Should().Be("Esta pantalla todavía no muestra qué documento concreto originó cada fila.");
    }

    /// <summary>
    /// <c>DeteccionTrabajadoresService</c> agrupa y compara los identificadores
    /// solo con <c>Trim().ToUpperInvariant()</c>: <c>12345678Z</c> y
    /// <c>12345678-Z</c> sobreviven como dos. La regla no puede prometer que
    /// «se quitan los DNI repetidos» sin más, y la columna «Comprobación» —que
    /// sí quita guiones y espacios— tiene que decir que eso es cosa suya.
    /// </summary>
    [Fact]
    public void La_regla_de_repetidos_dice_lo_que_hace_el_servicio_y_no_promete_unir_variantes_con_guion()
    {
        var escenario = new Escenario();
        escenario.Nuevo(EmpresaA, "Iker", "Mena Ruiz", "12345678Z");
        escenario.Nuevo(EmpresaA, "Jon", "Olano Sáez", "12345678-Z");
        var (cut, _) = Renderizar(escenario);

        cut.Markup.Should().NotContainEquivalentOf("se quitan los DNI repetidos",
            "el servicio solo quita los repetidos idénticos: la frase antigua prometía más de lo que hace");

        Texto(cut.Find(".deteccion-regla-identificadores")).Should().Be(
            "Los identificadores leídos se comparan tal cual, sin distinguir mayúsculas de minúsculas y sin contar los espacios del principio o del final: " +
            "de dos filas con el mismo identificador así comparado se queda una, pero el mismo DNI escrito de otra forma (12345678Z y 12345678-Z) " +
            "cuenta como dos, también al compararlo con la plantilla. " +
            "Un documento con más de 2.000 identificadores distintos se descarta entero y solo se propone un alta si el identificador es un DNI o NIE " +
            "con dígito de control correcto. Para las ausencias cuenta cualquier identificador leído, sea o no DNI/NIE, para no proponer la baja " +
            "de quien sí aparece con otro documento de identidad.",
            "la regla dice lo que hace DeteccionTrabajadoresService (Trim + ToUpperInvariant antes del GroupBy), sin prometer que une variantes con guion");

        Texto(cut.Find(".deteccion-ayuda-comprobacion")).Should().Be(
            "«Comprobación» es una comprobación de formato que hace esta pantalla: quita guiones y espacios y revisa el dígito de control. " +
            "La detección no los quita al comparar, así que puede tratar como distintas dos variantes del mismo DNI.");
    }

    [Fact]
    public void La_procedencia_cuenta_lo_pendiente_y_da_las_fechas_reales_de_deteccion()
    {
        var reciente = new DateTime(2026, 9, 5, 5, 12, 0, DateTimeKind.Utc);
        var antigua = new DateTime(2026, 8, 28, 16, 40, 0, DateTimeKind.Utc);
        var escenario = new Escenario();
        escenario.Nuevo(EmpresaA, "Iker", "Mena Ruiz", "12345678Z", reciente);
        escenario.Nuevo(EmpresaA, "Sofía", "Landa Vera", "87654321X", antigua);
        escenario.Ausente(EmpresaA, "Nuria", "Salas Prieto", "11223344B", reciente);
        var (cut, _) = Renderizar(escenario);

        cut.Find(".deteccion-pendientes").TextContent.Trim().Should().Be("3 (2 nuevos · 1 ausente)");
        cut.Find(".deteccion-fecha-reciente").TextContent.Trim().Should().Be(reciente.ToLocalTime().ToString("dd/MM/yyyy HH:mm"));
        cut.Find(".deteccion-fecha-antigua").TextContent.Trim().Should().Be(antigua.ToLocalTime().ToString("dd/MM/yyyy HH:mm"));
    }

    // ---------------------------------------------------------------- nuevos

    [Fact]
    public async Task Descartar_no_envia_nada_hasta_confirmar_y_confirmar_lo_descarta_una_vez()
    {
        var escenario = new Escenario();
        var iker = escenario.Nuevo(EmpresaA, "Iker", "Mena Ruiz", "12345678Z");
        escenario.Nuevo(EmpresaA, "Sofía", "Landa Vera", "87654321X");
        var (cut, mediador) = Renderizar(escenario);

        await Pulsar(cut, iker, "Descartar");

        Comandos(mediador).Should().BeEmpty("descartar cierra la detección sin forma de reabrirla: no sale de un clic");
        cut.Find("[role=dialog] h2").TextContent.Trim().Should().Be("¿Descartar a Iker Mena Ruiz?");
        cut.Find(".modal-cuerpo p").TextContent.Should().Contain("no se puede recuperar");

        await BotonDelDialogo(cut, "Descartar").ClickAsync(new MouseEventArgs());

        Comandos(mediador).Should().ContainSingle().Which.Should().Be(new ResolverDeteccionNuevoCommand(iker.Id, Crear: false));
        escenario.RegistroDe(iker).AccionTomada.Should().Be("Descartado");
        escenario.TrabajadoresCreados.Should().BeEmpty();
        cut.FindAll("[role=dialog]").Should().BeEmpty();
        NombresPintados(cut).Should().Equal(["Sofía Landa Vera"]);
    }

    [Fact]
    public async Task Cancelar_el_descarte_no_envia_nada_y_la_fila_sigue()
    {
        var escenario = new Escenario();
        var iker = escenario.Nuevo(EmpresaA, "Iker", "Mena Ruiz", "12345678Z");
        var (cut, mediador) = Renderizar(escenario);

        await Pulsar(cut, iker, "Descartar");
        // Antes de buscar «Cancelar»: sin la confirmación, el descarte ya habría
        // salido y el diálogo no existiría —rojo por no encontrar el botón, no
        // por lo que este test observa (medido por mutación el 2026-09-11)—.
        Comandos(mediador).Should().BeEmpty("pulsar «Descartar» solo abre la confirmación");

        await BotonDelDialogo(cut, "Cancelar").ClickAsync(new MouseEventArgs());

        Comandos(mediador).Should().BeEmpty();
        cut.FindAll("[role=dialog]").Should().BeEmpty();
        NombresPintados(cut).Should().Equal(["Iker Mena Ruiz"]);
    }

    [Fact]
    public async Task Dar_de_alta_es_un_clic_crea_a_esa_persona_y_la_fila_sale_de_la_lista()
    {
        var escenario = new Escenario();
        var iker = escenario.Nuevo(EmpresaA, "Iker", "Mena Ruiz", "12345678Z");
        escenario.Ausente(EmpresaA, "Nuria", "Salas Prieto", "11223344B");
        var (cut, mediador) = Renderizar(escenario);

        await Pulsar(cut, iker, "Dar de alta");

        Comandos(mediador).Should().ContainSingle().Which.Should().Be(new ResolverDeteccionNuevoCommand(iker.Id, Crear: true));
        escenario.TrabajadoresCreados.Should().Equal([(EmpresaA, "Iker Mena Ruiz", "12345678Z")]);
        cut.FindAll("[role=dialog]").Should().BeEmpty("crear un solo trabajador, que se puede eliminar después, no pide confirmación");
        UltimoToast().Mensaje.Should().Be("Iker Mena Ruiz dado de alta como trabajador.");
        UltimoToast().Tono.Should().Be(TonoToast.Exito);
        NombresPintados(cut).Should().Equal(["Nuria Salas Prieto"]);
        cut.FindAll("#deteccion-nuevos-titulo").Should().BeEmpty("sin nuevos pendientes, su sección no se pinta");
    }

    [Fact]
    public async Task Si_el_alta_falla_por_dni_duplicado_avisa_con_el_motivo_y_la_deteccion_sigue_pendiente()
    {
        var escenario = new Escenario();
        var iker = escenario.Nuevo(EmpresaA, "Iker", "Mena Ruiz", "12345678Z");
        escenario.DnisExistentes.Add("12345678Z");
        var (cut, _) = Renderizar(escenario);

        await Pulsar(cut, iker, "Dar de alta");

        var toast = UltimoToast();
        toast.Tono.Should().Be(TonoToast.Error);
        toast.Mensaje.Should().Be("Ya existe un trabajador con este DNI — revisa el listado de trabajadores.");
        escenario.RegistroDe(iker).Resuelta.Should().BeFalse();
        NombresPintados(cut).Should().Equal(["Iker Mena Ruiz"]);
        BotonDeFila(cut, iker, "Dar de alta").HasAttribute("disabled").Should().BeFalse("tras el fallo se puede reintentar");
    }

    // ---------------------------------------------------------------- una resolución a la vez

    /// <summary>
    /// Antes solo se marcaba la fila en curso y el resto de la tabla seguía
    /// pulsable: dos resoluciones podían viajar a la vez. Los clics de las
    /// otras filas no se esperan antes de comprobar: si la guarda fallara, el
    /// doble no los retiene y el test se pondría rojo en vez de colgarse.
    /// </summary>
    [Fact]
    public async Task Mientras_una_resolucion_viaja_ninguna_otra_fila_envia_nada()
    {
        var alta = new TaskCompletionSource();
        var escenario = new Escenario();
        var iker = escenario.Nuevo(EmpresaA, "Iker", "Mena Ruiz", "12345678Z");
        var sofia = escenario.Nuevo(EmpresaA, "Sofía", "Landa Vera", "87654321X");
        var nuria = escenario.Ausente(EmpresaA, "Nuria", "Salas Prieto", "11223344B");
        escenario.Retener = p => p is ResolverDeteccionNuevoCommand c && c.DeteccionId == iker.Id ? alta.Task : null;
        var (cut, mediador) = Renderizar(escenario);

        var primero = Pulsar(cut, iker, "Dar de alta");

        cut.FindAll("td .deteccion-acciones-fila button").Should().OnlyContain(b => b.HasAttribute("disabled"),
            "con una resolución en vuelo, ninguna acción de fila está disponible");

        // Se comprueba tras CADA clic: sin la guarda, el primero que se colara
        // completaría en el acto y sacaría su fila, y el clic siguiente fallaría
        // por no encontrarla —rojo por un motivo que no es el que este test
        // dice observar (medido por mutación el 2026-09-11)—.
        async Task PulsarYComprobarQueNoSale(DeteccionTrabajadorDto deteccion, string boton)
        {
            await Pulsar(cut, deteccion, boton);
            Comandos(mediador).Should().ContainSingle($"«{boton}» de {deteccion.Nombre} llegó con otra resolución en vuelo")
                .Which.Should().Be(new ResolverDeteccionNuevoCommand(iker.Id, Crear: true));
            cut.FindAll("[role=dialog]").Should().BeEmpty("tampoco se abre una confirmación para otra fila");
        }

        await PulsarYComprobarQueNoSale(sofia, "Dar de alta");
        await PulsarYComprobarQueNoSale(nuria, "Mantener activo");
        await PulsarYComprobarQueNoSale(nuria, "Dar de baja");
        await PulsarYComprobarQueNoSale(sofia, "Descartar");

        await cut.InvokeAsync(() => alta.SetResult());
        await primero;

        NombresPintados(cut).Should().Equal(["Sofía Landa Vera", "Nuria Salas Prieto"],
            "la del alta aplicada salió; las demás siguen pendientes porque nada se envió por ellas");
        BotonDeFila(cut, sofia, "Dar de alta").HasAttribute("disabled").Should().BeFalse("terminada la resolución, se vuelve a poder actuar");
    }

    [Fact]
    public async Task Un_doble_clic_en_dar_de_alta_envia_un_solo_comando()
    {
        var alta = new TaskCompletionSource();
        var escenario = new Escenario();
        var iker = escenario.Nuevo(EmpresaA, "Iker", "Mena Ruiz", "12345678Z");
        escenario.Retener = p => p is ResolverDeteccionNuevoCommand ? alta.Task : null;
        var (cut, mediador) = Renderizar(escenario);

        var primero = Pulsar(cut, iker, "Dar de alta");
        var segundo = Pulsar(cut, iker, "Dar de alta");

        Comandos(mediador).Should().ContainSingle("el segundo clic llega con el primero en vuelo");

        await cut.InvokeAsync(() => alta.SetResult());
        await Task.WhenAll(primero, segundo);

        Comandos(mediador).Should().ContainSingle();
    }

    // ---------------------------------------------------------------- cargas

    [Fact]
    public async Task La_lista_de_otra_empresa_que_llega_tarde_se_descarta()
    {
        var listaA = new TaskCompletionSource<object?>();
        var escenario = new Escenario();
        escenario.Nuevo(EmpresaA, "Iker", "Mena Ruiz", "12345678Z");
        escenario.Nuevo(EmpresaB, "Lucía", "Nogal Pérez", "X1234567L");
        escenario.Interceptar = (p, _) => p is ObtenerDeteccionesPorEmpresaQuery q && q.EmpresaId == EmpresaA ? listaA.Task : null;
        var (cut, mediador) = Renderizar(escenario, EmpresaA);

        cut.Render(p => p.Add(c => c.EmpresaId, EmpresaB));
        await cut.InvokeAsync(() => listaA.SetResult(escenario.Lista(EmpresaA)));

        mediador.Enviados.OfType<ObtenerDeteccionesPorEmpresaQuery>().Select(q => q.EmpresaId).Should().Equal([EmpresaA, EmpresaB]);
        NombresPintados(cut).Should().Equal(["Lucía Nogal Pérez"], "la página es ya la de la empresa B: la lista de A llegó tarde y no es suya");
        cut.Find("h1").TextContent.Trim().Should().Be("Detección de personal — Frío Industrial Aragón S.L.");
    }

    [Fact]
    public async Task Si_la_carga_falla_reintentar_la_recupera()
    {
        var llamadas = 0;
        var escenario = new Escenario();
        escenario.Nuevo(EmpresaA, "Iker", "Mena Ruiz", "12345678Z");
        escenario.Interceptar = (p, _) => p is ObtenerDeteccionesPorEmpresaQuery && ++llamadas == 1
            ? Task.FromException<object?>(new InvalidOperationException("Base de datos caída (simulada)."))
            : null;
        var (cut, _) = Renderizar(escenario);

        cut.Find(".estado-vacio h3").TextContent.Trim().Should().Be("No pudimos cargar las detecciones");

        await cut.FindAll(".estado-vacio button").Single(b => Texto(b) == "Reintentar").ClickAsync(new MouseEventArgs());

        llamadas.Should().Be(2);
        NombresPintados(cut).Should().Equal(["Iker Mena Ruiz"]);
    }

    /// <summary>
    /// «Sin cambios» y «nadie está mirando» se parecían demasiado: el vacío dice
    /// ahora de qué depende que la detección corra, y a quien puede activarla
    /// le dice dónde. Las dos pantallas son de Administrador.
    /// </summary>
    [Theory]
    [InlineData(Roles.Administrador, true)]
    [InlineData(Roles.GestorCae, false)]
    public void Sin_detecciones_el_vacio_dice_de_que_depende_y_solo_el_administrador_ve_donde_activarla(string rol, bool veEnlaces)
    {
        var (cut, _) = Renderizar(new Escenario(), rol: rol);

        cut.Find(".estado-vacio h3").TextContent.Trim().Should().Be("Sin cambios pendientes");
        cut.Find(".estado-vacio p").TextContent.Should().Contain("no garantiza que se haya leído ningún documento")
            .And.Contain("Clientes empresariales");

        var enlaces = cut.FindAll(".estado-vacio a").Select(a => a.GetAttribute("href")).ToList();
        if (veEnlaces)
            enlaces.Should().Equal(["/tipos-documento", "/configuracion/lectura-ia"]);
        else
            enlaces.Should().BeEmpty("mandar al Gestor CAE a una pantalla de Administrador es mandarlo a un acceso denegado");
    }

    /// <summary>
    /// bUnit 2.x no llama al <c>Dispose</c> del componente con <c>cut.Dispose()</c>:
    /// se retira con <see cref="BunitContext.DisposeComponentsAsync"/>, y
    /// después no deja leer <c>cut.Instance</c>: la instancia se toma antes.
    ///
    /// <para>
    /// La carga de detecciones queda EN VUELO al retirar la página: el doble
    /// la retiene con un <see cref="TaskCompletionSource{TResult}"/> y honra el
    /// token como lo haría EF —al cancelarse, la consulta termina cancelada—.
    /// Así el test observa que <c>Dispose</c> cancela el token con el que
    /// viajó ESA carga, no el de una consulta que ya había terminado.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Al_retirar_la_pagina_se_cancela_la_carga_de_detecciones_en_vuelo_y_no_pinta_ni_recarga()
    {
        var lista = new TaskCompletionSource<object?>();
        CancellationToken? tokenDeLaCarga = null;
        var escenario = new Escenario();
        escenario.Nuevo(EmpresaA, "Iker", "Mena Ruiz", "12345678Z");
        escenario.Interceptar = (p, token) =>
        {
            if (p is not ObtenerDeteccionesPorEmpresaQuery)
                return null;

            tokenDeLaCarga = token;
            token.Register(() => lista.TrySetCanceled(token));
            return lista.Task;
        };
        var (cut, mediador) = Renderizar(escenario);

        cut.FindAll(".esqueleto-lista").Should().ContainSingle("la carga de detecciones sigue retenida: la página está esperando");
        tokenDeLaCarga.Should().NotBeNull("la carga de detecciones llegó al doble");
        tokenDeLaCarga!.Value.CanBeCanceled.Should().BeTrue(
            "la carga tiene que viajar con el token del ciclo de la página: con CancellationToken.None, retirarla no la cancela");
        tokenDeLaCarga.Value.IsCancellationRequested.Should().BeFalse("antes de retirar la página nadie ha cancelado nada");
        var instancia = cut.Instance;

        await DisposeComponentsAsync();

        tokenDeLaCarga.Value.IsCancellationRequested.Should().BeTrue(
            "Dispose tiene que cancelar el token con el que viajó la carga de detecciones en vuelo");
        lista.Task.IsCanceled.Should().BeTrue("el doble honra el token: la consulta retenida terminó cancelada");

        // Si la respuesta llegara aun así, no hay nada que escribir: la
        // consulta ya se canceló (TrySetResult no la cambia) y la página no la espera.
        Func<Task> llegaTarde = () => Renderer.Dispatcher.InvokeAsync(() => lista.TrySetResult(escenario.Lista(EmpresaA)));
        await llegaTarde.Should().NotThrowAsync();

        Renderer.UnhandledException.IsCompleted.Should().BeFalse("la cancelación por retirada se absorbe: no sale como excepción no controlada");
        Campo<bool>(instancia, "_errorCarga").Should().BeFalse("una carga cancelada por retirar la página no es un fallo de carga");
        Campo<bool>(instancia, "_listaPintada").Should().BeFalse("la página retirada no pinta ninguna lista");
        Campo<IReadOnlyList<DeteccionTrabajadorDto>>(instancia, "_detecciones").Should().BeEmpty();
        mediador.Enviados.OfType<ObtenerDeteccionesPorEmpresaQuery>().Should().ContainSingle(
            "la página ya no existe: no se vuelve a pedir una lista que nadie va a ver");
        mediador.Enviados.OfType<ObtenerEmpresaPorIdQuery>().Should().ContainSingle();
    }

    /// <summary>
    /// Un handler que ya pasó su último punto de cancelación responde igual,
    /// aunque el token esté cancelado: aquí el doble retiene la consulta SIN
    /// mirar el token. Lo que impide que esa respuesta tardía siga la carga es
    /// la comprobación de vigencia tras cada <c>await</c>, no la cancelación.
    /// </summary>
    [Theory]
    [InlineData(nameof(ObtenerEmpresaPorIdQuery))]
    [InlineData(nameof(ObtenerDeteccionesPorEmpresaQuery))]
    public async Task Una_respuesta_que_llega_tras_retirar_la_pagina_no_pinta_ni_pide_mas(string consultaTardia)
    {
        var respuesta = new TaskCompletionSource();
        var escenario = new Escenario();
        escenario.Nuevo(EmpresaA, "Iker", "Mena Ruiz", "12345678Z");
        escenario.Retener = p => p.GetType().Name == consultaTardia ? respuesta.Task : null;
        var (cut, mediador) = Renderizar(escenario);

        cut.FindAll(".esqueleto-lista").Should().ContainSingle($"{consultaTardia} sigue retenida: la página está esperando");
        var enviadasAntes = mediador.Enviados.Count();
        var instancia = cut.Instance;

        await DisposeComponentsAsync();

        Func<Task> llegaTarde = () => Renderer.Dispatcher.InvokeAsync(() => respuesta.SetResult());
        await llegaTarde.Should().NotThrowAsync("la respuesta tardía no puede tocar un componente ya retirado");

        mediador.Enviados.Should().HaveCount(enviadasAntes,
            "retirada la página, una respuesta tardía no sigue la carga: no pide nada más");
        Campo<bool>(instancia, "_listaPintada").Should().BeFalse("la página retirada no pinta la lista que llegó tarde");
        Campo<IReadOnlyList<DeteccionTrabajadorDto>>(instancia, "_detecciones").Should().BeEmpty();
        Campo<EmpresaDetalleDto?>(instancia, "_empresa").Should().BeNull();
        Campo<bool>(instancia, "_errorCarga").Should().BeFalse();
        Renderer.UnhandledException.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task Una_resolucion_que_termina_tras_retirar_la_pagina_no_recarga_la_lista()
    {
        var mantener = new TaskCompletionSource();
        var escenario = new Escenario();
        var nuria = escenario.Ausente(EmpresaA, "Nuria", "Salas Prieto", "11223344B");
        escenario.Retener = p => p is ResolverDeteccionAusenteCommand ? mantener.Task : null;
        var (cut, mediador) = Renderizar(escenario);

        var resolucion = Pulsar(cut, nuria, "Mantener activo");
        await DisposeComponentsAsync();

        mantener.SetResult();
        await resolucion;

        mediador.Enviados.OfType<ObtenerDeteccionesPorEmpresaQuery>().Should().ContainSingle(
            "la página ya no existe: no se vuelve a pedir una lista que nadie va a ver");
        UltimoToast().Mensaje.Should().Be("Trabajador mantenido activo.", "la resolución sí se aplicó, y eso se sigue diciendo");
    }

    // ---------------------------------------------------------------- atajos

    [Fact]
    public async Task J_y_k_recorren_una_sola_secuencia_primero_nuevos_y_despues_ausentes()
    {
        var escenario = new Escenario();
        var iker = escenario.Nuevo(EmpresaA, "Iker", "Mena Ruiz", "12345678Z");
        var nuria = escenario.Ausente(EmpresaA, "Nuria", "Salas Prieto", "11223344B");
        var (cut, _) = Renderizar(escenario);
        var atajos = cut.FindComponent<AtajosListaTeclado>().Instance;

        string? Enfocada() => cut.FindAll("tr.fila-enfocada").SingleOrDefault()?.GetAttribute("data-deteccion");

        await cut.InvokeAsync(() => atajos.RecibirAtajo("j"));
        Enfocada().Should().Be(iker.Id.ToString());

        await cut.InvokeAsync(() => atajos.RecibirAtajo("j"));
        Enfocada().Should().Be(nuria.Id.ToString(), "de la última fila de nuevos se pasa a la primera de ausentes");

        await cut.InvokeAsync(() => atajos.RecibirAtajo("j"));
        Enfocada().Should().Be(nuria.Id.ToString(), "al final de la secuencia se queda en la última");

        await cut.InvokeAsync(() => atajos.RecibirAtajo("k"));
        Enfocada().Should().Be(iker.Id.ToString());
    }
}
