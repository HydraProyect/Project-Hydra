using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Operaciones.ApoyoCartera.Commands;
using CaeManager.Application.Operaciones.ApoyoCartera.Queries;
using CaeManager.Application.Usuarios.Queries.ObtenerOperadoresCaeDeMiTenant;
using CaeManager.Application.Usuarios.Queries.ObtenerPersonasConCartera;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Empresas.Components;
using CaeManager.Web.Features.IncorporacionCartera.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Dato de cabecera «Gestor CAE» de la pantalla Empresas (decisiones 2026-10-08 y 2026-10-09):
/// quién gestiona el Tenant propietario activo — el Gestor CAE principal y los Gestores CAE de
/// apoyo, que son personas (el Operador CAE es la organización).
///
/// <para>
/// El dato va en UNA línea: salen por su nombre <see cref="CabeceraGestorCae.PersonasALaVista"/>
/// personas y las demás se agrupan en «+N», cuya ventana enseña la cartera entera. Que la línea
/// no envuelva es CSS (<c>CabeceraGestorCae.razor.css</c>) y bUnit no lo mide: aquí se fija la
/// estructura que lo hace posible y que no se pierde ningún nombre.
/// </para>
///
/// <para>
/// Las dos acciones que acompañan al dato —«+ Dar acceso» y «Desasignarme»— las pinta
/// <see cref="PanelDarAcceso"/> en su forma de cabecera. Qué puede ver cada quien lo deciden
/// <c>ObtenerPersonasConCarteraQuery</c> (cuenta de gestión CAE del Operador CAE externo),
/// <c>ObtenerApoyosDeCarteraQuery</c> y <c>ObtenerOperadoresCaeDeMiTenantQuery</c> (Administrador
/// del Tenant propietario), y quién puede cada cosa, los Commands: eso se prueba en Application.
/// Aquí, qué se pinta con lo que esas consultas devuelven y qué Command se envía.
/// </para>
/// </summary>
public class CabeceraGestorCaeTests : BunitContext
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid OtroTenant = Guid.NewGuid();

    /// <summary>La persona que mira la pantalla.</summary>
    private static readonly Guid Yo = Guid.NewGuid();

    private MediadorPorFuncion _mediador = default!;

    public CabeceraGestorCaeTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class TenantFijo(Guid? id) : ITenantActual
    {
        public Guid? TenantId { get; } = id;
    }

    private sealed class UsuarioFijo(Guid id) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(id);
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(Roles.GestorCae);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    /// <param name="responder">Lo que devuelve la lectura del Operador CAE (la hacen la cabecera y el panel de acciones).</param>
    /// <param name="comoPropietario">Lo que devuelve la lectura del Administrador del Tenant propietario.</param>
    /// <param name="apoyos">Los apoyos vivos sobre los que quien mira puede hacer algo.</param>
    /// <param name="rol">Rol de la sesión: decide lo que <c>SoloConEscritura</c> deja ver.</param>
    /// <param name="otras">Respuesta a los Commands y a las lecturas del formulario; sin ella, cualquier otra petición es un fallo.</param>
    private IRenderedComponent<CabeceraGestorCae> Renderizar(
        Func<ObtenerPersonasConCarteraQuery, IReadOnlyList<CarterasDeOperacion>> responder, Guid? tenant = null, bool sinTenant = false,
        Func<IReadOnlyList<CarterasDeOperacion>>? comoPropietario = null,
        Func<ApoyosDeCarteraDto>? apoyos = null, string rol = Roles.GestorCae, Func<object, object?>? otras = null)
    {
        this.ConRolDeEscritura(rol);
        Services.AddLocalization();
        Services.AddScoped<ToastService>();
        Services.AddScoped<ICurrentUserService>(_ => new UsuarioFijo(Yo));
        Services.AddScoped<ITenantActual>(_ => new TenantFijo(sinTenant ? null : tenant ?? Tenant));
        _mediador = new MediadorPorFuncion(p => p switch
        {
            ObtenerPersonasConCarteraQuery q => responder(q),
            ObtenerOperadoresCaeDeMiTenantQuery => (comoPropietario ?? (() => []))(),
            ObtenerPropuestasApoyoPendientesQuery => PropuestasApoyoPendientesDto.Vacia,
            ObtenerApoyosDeCarteraQuery => (apoyos ?? (() => ApoyosDeCarteraDto.Vacio))(),
            _ => otras?.Invoke(p) ?? throw new InvalidOperationException($"Consulta inesperada: {p.GetType().Name}")
        });
        Services.AddScoped<IMediator>(_ => _mediador);
        return Render<CabeceraGestorCae>();
    }

    private static PersonaConCartera Persona(string nombre, string rol = Roles.GestorCae, DateTime? hasta = null, string? avatar = null) =>
        new(Guid.NewGuid(), nombre, rol, hasta, avatar);

    private static PersonaConCartera YoComo(string rol = Roles.GestorCae) => new(Yo, "Nahia Urrutia", rol, null);

    private static CarterasDeOperacion Operacion(PersonaConCartera? principal, params PersonaConCartera[] apoyos) =>
        new(Guid.NewGuid(), Tenant, "Talleres Norte", principal, apoyos);

    /// <summary>Una operación como la devuelve la lectura del Administrador del Tenant propietario: con su Operador CAE.</summary>
    private static CarterasDeOperacion OperacionDe(string operador, PersonaConCartera? principal, params PersonaConCartera[] apoyos) =>
        new(Guid.NewGuid(), Tenant, string.Empty, principal, apoyos, operador);

    /// <summary>Un apoyo vivo de <paramref name="apoyo"/> sobre <paramref name="operacion"/>, tal y como lo da la Query.</summary>
    private static ApoyoDeCarteraDto ApoyoVivo(CarterasDeOperacion operacion, PersonaConCartera apoyo, PersonaConCartera proponente) =>
        new(Guid.NewGuid(), operacion.TenantId, operacion.NombreTenant, operacion.AsignacionOperacionId,
            apoyo.UsuarioId, apoyo.Nombre, proponente.UsuarioId, proponente.Nombre, null);

    private static List<string> LineasDelDetalle(IRenderedComponent<CabeceraGestorCae> cut, string clase) =>
        cut.FindAll($".ventana-contexto-panel [data-gestor-cae-detalle='{clase}']").Select(l => l.TextContent.Trim()).ToList();

    private int LecturasDeLaCartera() => _mediador.Enviadas.OfType<ObtenerPersonasConCarteraQuery>().Count();

    // ---------- La línea: quién sale y con qué rótulo ----------

    [Fact]
    public void Pinta_al_principal_y_al_de_apoyo_con_la_fecha_de_fin_de_quien_la_tiene()
    {
        ObtenerPersonasConCarteraQuery? pedida = null;
        var cut = Renderizar(q =>
        {
            pedida = q;
            return [Operacion(
                Persona("Marta Ibarra", avatar: "buho-ambar"),
                Persona("Unai Zabala", hasta: new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc)))];
        });

        pedida!.TenantId.Should().Be(Tenant, "se pregunta por el Tenant propietario activo, no por todos los que opera");

        cut.Find(".cabecera-gestor-cae-rotulo").TextContent.Trim().Should().Be("Gestor CAE");

        var principal = cut.Find("[data-gestor-cae='principal']");
        principal.QuerySelector("strong")!.TextContent.Trim().Should().Be("Marta Ibarra");
        principal.QuerySelector(".cabecera-gestor-cae-pastilla")!.TextContent.Trim().Should().Be("Principal");
        principal.QuerySelectorAll(".avatar-usuario-glifo").Should().ContainSingle("lleva el avatar que eligió");

        var apoyo = cut.FindAll("[data-gestor-cae='apoyo']").Should().ContainSingle().Subject;
        apoyo.QuerySelector(".cabecera-gestor-cae-pastilla")!.TextContent.Trim().Should().Be("Apoyo hasta el 31/12/2026");
        apoyo.TextContent.Should().Contain("Unai Zabala");
        apoyo.QuerySelector(".avatar-usuario")!.TextContent.Trim().Should().Be("UZ", "sin avatar elegido, sus iniciales");

        cut.FindAll("[data-gestor-cae-mas]").Should().BeEmpty("dos personas caben en la línea: no hay nada que agrupar");
        cut.FindAll("button, a, input").Should().BeEmpty("quien mira no es el principal ni tiene un apoyo propio: no se le ofrece nada");
    }

    /// <summary>
    /// UNA línea (cierre de listados, 2026-10-09): con más personas de las que caben junto al título, salen
    /// por su nombre las dos primeras —los principales antes que los de apoyo— y las demás van a «+N». La
    /// ventana de «+N» enseña a TODAS con su rótulo, y la línea lleva la cartera entera en su `title`: no se
    /// pierde ningún nombre ni ninguna fecha de fin.
    /// </summary>
    [Fact]
    public void Con_mas_personas_de_las_que_caben_las_demas_van_a_una_pastilla_que_abre_la_cartera_entera()
    {
        var cut = Renderizar(_ => [Operacion(
            Persona("Marta Ibarra", avatar: "buho-ambar"),
            Persona("Unai Zabala", hasta: new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc)),
            Persona("Ane Larrea"),
            Persona("Jon Etxeberria"))]);

        CabeceraGestorCae.PersonasALaVista.Should().Be(2, "control: el test está escrito para ese tope");
        cut.FindAll(".cabecera-gestor-cae-personas .cabecera-gestor-cae-persona")
            .Select(p => (p.GetAttribute("data-gestor-cae"), p.QuerySelector(".cabecera-gestor-cae-nombre")!.TextContent.Trim()))
            .Should().Equal([("principal", "Marta Ibarra"), ("apoyo", "Ane Larrea")], "el principal y el primer apoyo por nombre");
        cut.FindAll(".cabecera-gestor-cae-nombre").Should().OnlyContain(
            n => n.GetAttribute("title") == n.TextContent.Trim(), "un nombre recortado por falta de sitio se lee entero en su title");

        var mas = cut.Find("[data-gestor-cae-mas]");
        mas.GetAttribute("data-gestor-cae-mas").Should().Be("2");
        mas.QuerySelector(".cabecera-gestor-cae-mas")!.TextContent.Trim().Should().Be("+2");
        mas.GetAttribute("aria-label").Should().Be("2 más. Lista completa de personas con Asignación de Cartera");
        mas.Closest(".cabecera-gestor-cae-personas").Should().BeNull("fuera de la caja que recorta: su panel no puede quedar cortado");

        LineasDelDetalle(cut, "principal").Should().Equal("Marta Ibarra · Principal");
        LineasDelDetalle(cut, "apoyo").Should().Equal(
            ["Ane Larrea · Apoyo", "Jon Etxeberria · Apoyo", "Unai Zabala · Apoyo hasta el 31/12/2026"],
            "la ventana enseña a todas, también a las que ya salen en la línea");
        cut.Find(".cabecera-gestor-cae-personas").GetAttribute("title").Should().Be(
            "Marta Ibarra (Principal) · Ane Larrea (Apoyo) · Jon Etxeberria (Apoyo) · Unai Zabala (Apoyo hasta el 31/12/2026)");

        cut.FindAll("button, a, input").Should().BeEmpty("la ventana de «+N» es de lectura: no añade ninguna acción");
    }

    [Fact]
    public void Un_principal_que_gestiona_como_Coordinador_CAE_se_rotula_asi()
    {
        var cut = Renderizar(_ => [Operacion(Persona("Iker Sola", Roles.CoordinadorCae))]);

        cut.Find("[data-gestor-cae='principal'] .cabecera-gestor-cae-pastilla").TextContent.Trim()
            .Should().Be("Coordinador CAE principal");
    }

    /// <summary>
    /// Con más de una Asignación de Operación viva sobre el Tenant, cada principal se rotula
    /// como principal (llamar «apoyo» al de la segunda sería falso) y nadie sale dos veces:
    /// quien es principal de una no se repite como apoyo de otra. Son tres personas: los dos
    /// principales ocupan la línea y la de apoyo va a «+1».
    /// </summary>
    [Fact]
    public void Con_varias_Asignaciones_de_Operacion_cada_principal_lo_es_y_nadie_se_repite()
    {
        var marta = Persona("Marta Ibarra");
        var iker = Persona("Iker Sola", Roles.CoordinadorCae);
        var ane = Persona("Ane Larrea");
        var cut = Renderizar(_ =>
        [
            Operacion(marta, ane, iker with { Rol = Roles.GestorCae }),
            Operacion(iker, ane, marta)
        ]);

        cut.FindAll("[data-gestor-cae='principal']").Select(p => p.QuerySelector("strong")!.TextContent.Trim())
            .Should().Equal(["Iker Sola", "Marta Ibarra"], "los dos principales, por nombre");
        cut.FindAll("[data-gestor-cae='apoyo']").Should().BeEmpty("los principales van antes: la de apoyo no cabe en la línea");
        cut.Find("[data-gestor-cae-mas]").GetAttribute("data-gestor-cae-mas").Should().Be("1");
        LineasDelDetalle(cut, "principal").Should().Equal("Iker Sola · Coordinador CAE principal", "Marta Ibarra · Principal");
        LineasDelDetalle(cut, "apoyo").Should().Equal(["Ane Larrea · Apoyo"], "nadie sale dos veces: ni Ane repetida ni un principal como apoyo");
        cut.FindAll("[data-gestor-cae='sin-principal']").Should().BeEmpty();
        cut.FindAll("[data-gestor-cae='otra-sin-principal']").Should().BeEmpty("las dos tienen principal");
        LineasDelDetalle(cut, "otra-sin-principal").Should().BeEmpty();
    }

    /// <summary>
    /// Caso declarado en #1163: dos Asignaciones de Operación vivas y solo una con principal.
    /// Pintar al principal de una y callar la otra haría creer que las dos lo tienen.
    /// </summary>
    [Fact]
    public void Con_varias_Asignaciones_de_Operacion_y_una_sin_principal_lo_dice()
    {
        var cut = Renderizar(_ => [Operacion(Persona("Marta Ibarra")), Operacion(null, Persona("Ane Larrea"))]);

        cut.FindAll("[data-gestor-cae='principal']").Should().ContainSingle();
        cut.Find("[data-gestor-cae='otra-sin-principal']").TextContent.Trim()
            .Should().Be("Otra Asignación de Operación, sin principal");
        cut.FindAll("[data-gestor-cae='sin-principal']").Should().BeEmpty("hay un principal: «Sin principal» a secas sería falso");
        cut.FindAll("[data-gestor-cae='apoyo']").Should().ContainSingle();
    }

    /// <summary>El aviso no cuenta para el tope: con la línea llena de personas, se sigue diciendo, en la línea y en la ventana.</summary>
    [Fact]
    public void El_aviso_de_otra_Asignacion_de_Operacion_sin_principal_se_dice_aunque_la_linea_este_llena()
    {
        var cut = Renderizar(_ =>
        [
            Operacion(Persona("Marta Ibarra"), Persona("Ane Larrea"), Persona("Unai Zabala")),
            Operacion(null, Persona("Jon Etxeberria"))
        ]);

        cut.Find("[data-gestor-cae-mas]").GetAttribute("data-gestor-cae-mas").Should().Be("2", "control: hay personas agrupadas");
        cut.Find(".cabecera-gestor-cae-personas [data-gestor-cae='otra-sin-principal']").TextContent.Trim()
            .Should().Be("Otra Asignación de Operación, sin principal");
        LineasDelDetalle(cut, "otra-sin-principal").Should().ContainSingle();
    }

    /// <summary>
    /// La misma persona como principal de dos Asignaciones de Operación sale una sola vez: dos
    /// elementos con la misma clave matarían el circuito de Blazor.
    /// </summary>
    [Fact]
    public void Quien_es_principal_de_dos_Asignaciones_de_Operacion_sale_una_sola_vez()
    {
        var marta = Persona("Marta Ibarra");
        var cut = Renderizar(_ => [Operacion(marta), Operacion(marta, Persona("Ane Larrea"))]);

        cut.FindAll("[data-gestor-cae='principal']").Should().ContainSingle();
        cut.FindAll("[data-gestor-cae='apoyo']").Should().ContainSingle();
    }

    /// <summary>Sin principal es un estado válido: se dice, y los de apoyo siguen saliendo.</summary>
    [Fact]
    public void Sin_principal_lo_dice_y_sigue_pintando_a_los_de_apoyo()
    {
        var cut = Renderizar(_ => [Operacion(null, Persona("Ane Larrea"))]);

        cut.Find("[data-gestor-cae='sin-principal']").TextContent.Trim().Should().Be("Sin principal");
        cut.FindAll("[data-gestor-cae='principal']").Should().BeEmpty();
        cut.FindAll("[data-gestor-cae='apoyo']").Should().ContainSingle();
        cut.FindAll("[data-gestor-cae='operador']").Should().BeEmpty("a quien mira desde el Operador CAE no se le nombra su propia organización");
    }

    // ---------- Acciones: «+ Dar acceso» y «Desasignarme» (decisión 2026-10-08) ----------

    /// <summary>
    /// «+ Dar acceso» se le ofrece a quien es hoy el principal de la operación, y a nadie más. Lo decide
    /// <see cref="PanelDarAcceso"/> con la misma lectura que en /cartera/solicitudes.
    /// </summary>
    [Fact]
    public void El_principal_de_la_operacion_ve_Dar_acceso_y_no_Desasignarme()
    {
        var mia = Operacion(YoComo(), Persona("Ane Larrea"));
        var cut = Renderizar(_ => [mia]);

        cut.Find("[data-gestor-cae='principal'] strong").TextContent.Trim().Should().Be("Nahia Urrutia", "control: quien mira es el principal");
        cut.FindAll("[data-dar-acceso-cabecera]").Should().ContainSingle()
            .Which.GetAttribute("data-dar-acceso-cabecera").Should().Be(mia.AsignacionOperacionId.ToString());
        cut.Find("[data-dar-acceso-cabecera]").TextContent.Trim().Should().Be("+ Dar acceso");
        cut.FindAll("[data-desasignarme-cabecera]").Should().BeEmpty("el principal no tiene un apoyo del que desasignarse");
        cut.Find("[data-dar-acceso-cabecera]").Closest(".cabecera-gestor-cae").Should().BeNull(
            "los botones van a continuación del dato, no dentro de la caja que recorta los nombres");
        cut.FindAll("[data-testid='panel-dar-acceso'], [data-testid='panel-mis-apoyos']").Should().BeEmpty(
            "en la cabecera el panel solo pinta los botones, no sus tarjetas");
    }

    /// <summary>«Desasignarme» se le ofrece a quien tiene un apoyo propio sobre este Tenant.</summary>
    [Fact]
    public void Quien_tiene_un_apoyo_propio_ve_Desasignarme_y_no_Dar_acceso()
    {
        var marta = Persona("Marta Ibarra");
        var ajena = Operacion(marta, YoComo());
        var mio = ApoyoVivo(ajena, YoComo(), marta);
        var cut = Renderizar(_ => [ajena], apoyos: () => new ApoyosDeCarteraDto([mio], [], []));

        _mediador.Enviadas.OfType<ObtenerApoyosDeCarteraQuery>().Should().ContainSingle()
            .Which.TenantId.Should().Be(Tenant, "solo los apoyos sobre el Tenant propietario activo");
        cut.FindAll("[data-desasignarme-cabecera]").Should().ContainSingle()
            .Which.GetAttribute("data-desasignarme-cabecera").Should().Be(mio.PropuestaId.ToString());
        cut.Find("[data-desasignarme-cabecera]").TextContent.Trim().Should().Be("Desasignarme");
        cut.FindAll("[data-dar-acceso-cabecera]").Should().BeEmpty("no es el principal: no puede dar acceso");
    }

    /// <summary>
    /// HUECO DECLARADO, no arreglado aquí: un Coordinador CAE con cartera sobre el Tenant que no es el
    /// principal no ve «+ Dar acceso» (solo propone el principal: <c>ProponerApoyoCarteraCommand</c>) ni
    /// «Desasignarme» (su cartera no viene de un apoyo). Los apoyos que puede revocar por su rol siguen en
    /// /cartera/solicitudes: la cabecera no ofrece «Revocar».
    /// </summary>
    [Fact]
    public void Un_Coordinador_CAE_que_no_es_el_principal_no_ve_ninguna_de_las_dos_acciones()
    {
        var marta = Persona("Marta Ibarra");
        var ane = Persona("Ane Larrea");
        var ajena = Operacion(marta, ane, YoComo(Roles.CoordinadorCae));
        var cut = Renderizar(_ => [ajena], rol: Roles.CoordinadorCae,
            apoyos: () => new ApoyosDeCarteraDto([], [], [ApoyoVivo(ajena, ane, marta)]));

        cut.FindAll("[data-gestor-cae-detalle='apoyo']").Select(l => l.TextContent.Trim())
            .Should().Contain("Nahia Urrutia · Apoyo", "control: quien mira tiene cartera sobre el Tenant");
        _mediador.Enviadas.OfType<ObtenerApoyosDeCarteraQuery>().Should().ContainSingle("control: el panel de acciones se montó y leyó");
        cut.FindAll("button").Should().BeEmpty("ni «+ Dar acceso», ni «Desasignarme», ni «Revocar»");
    }

    /// <summary>Con un rol que no escribe, «Desasignarme» no se ofrece: el Command lo denegaría igual.</summary>
    [Fact]
    public void Con_un_rol_sin_escritura_no_se_ofrece_Desasignarme()
    {
        var marta = Persona("Marta Ibarra");
        var ajena = Operacion(marta, YoComo());
        var cut = Renderizar(_ => [ajena], rol: Roles.Consulta,
            apoyos: () => new ApoyosDeCarteraDto([ApoyoVivo(ajena, YoComo(), marta)], [], []));

        cut.FindAll("[data-gestor-cae='apoyo']").Should().ContainSingle("control positivo: el dato se sigue viendo");
        cut.FindAll("button").Should().BeEmpty();
    }

    /// <summary>
    /// «+ Dar acceso» abre el mismo formulario y envía el mismo Command que en /cartera/solicitudes; al
    /// terminar, la cabecera vuelve a leer la cartera.
    /// </summary>
    [Fact]
    public async Task Dar_acceso_desde_la_cabecera_propone_el_apoyo_sobre_esa_operacion_y_la_cabecera_se_vuelve_a_leer()
    {
        var mia = Operacion(YoComo());
        var lucia = new DestinatarioDeApoyoDto(Guid.NewGuid(), "Lucía Garmendia");
        var propuesto = false;
        // Tras proponer, la lectura trae un apoyo más: solo una cabecera que relee puede pintarlo.
        var cut = Renderizar(
            _ => [propuesto ? mia with { Apoyos = [Persona("Lucía Garmendia")] } : mia],
            otras: p => p switch
            {
                ObtenerDestinatariosDeApoyoQuery => (IReadOnlyList<DestinatarioDeApoyoDto>)[lucia],
                ProponerApoyoCarteraCommand => PropuestaHecha(),
                _ => null
            });
        Result<Guid> PropuestaHecha()
        {
            propuesto = true;
            return Result.Exito(Guid.NewGuid());
        }

        cut.FindAll("[data-gestor-cae='apoyo']").Should().BeEmpty("punto de partida: sin apoyos");
        var lecturasAntes = LecturasDeLaCartera();

        await cut.InvokeAsync(() => cut.Find("[data-dar-acceso-cabecera]").Click());

        _mediador.Enviadas.OfType<ObtenerDestinatariosDeApoyoQuery>().Should().ContainSingle()
            .Which.AsignacionOperacionId.Should().Be(mia.AsignacionOperacionId);
        _mediador.Enviadas.OfType<ProponerApoyoCarteraCommand>().Should().BeEmpty("abrir el formulario no propone nada");

        cut.Find("select").Change(lucia.UsuarioId.ToString());
        await cut.InvokeAsync(() => cut.FindAll("button").Single(b => b.TextContent.Trim() == "Proponer").Click());

        _mediador.Enviadas.OfType<ProponerApoyoCarteraCommand>().Should().ContainSingle()
            .Which.Should().Be(new ProponerApoyoCarteraCommand(mia.AsignacionOperacionId, lucia.UsuarioId));
        cut.FindComponent<DrawerFormulario>().Instance.Visible.Should().BeFalse("propuesta enviada, el formulario se cierra");
        cut.WaitForAssertion(() => cut.FindAll("[data-gestor-cae='apoyo']").Should().ContainSingle()
            .Which.TextContent.Should().Contain("Lucía Garmendia", "la cabecera releyó la cartera al terminar la acción"));
        LecturasDeLaCartera().Should().Be(lecturasAntes + 2, "una relectura del panel y otra de la cabecera, ni más ni menos");
    }

    /// <summary>
    /// «Desasignarme» pregunta antes (quitarse un Tenant no se deshace), envía su Command con ESE apoyo y,
    /// al terminar, la cabecera vuelve a leer: quien se desasignó deja de salir.
    /// </summary>
    [Fact]
    public async Task Desasignarme_desde_la_cabecera_pregunta_envia_ese_apoyo_y_la_cabecera_se_vuelve_a_leer()
    {
        var marta = Persona("Marta Ibarra");
        var ajena = Operacion(marta, YoComo());
        var mio = ApoyoVivo(ajena, YoComo(), marta);
        var desasignado = false;
        var cut = Renderizar(
            _ => [desasignado ? ajena with { Apoyos = [] } : ajena],
            apoyos: () => desasignado ? ApoyosDeCarteraDto.Vacio : new ApoyosDeCarteraDto([mio], [], []),
            otras: p => p is DesasignarmeDeApoyoCommand ? Hecho() : null);
        Result Hecho()
        {
            desasignado = true;
            return Result.Exito();
        }

        cut.Find("[data-gestor-cae='apoyo']").TextContent.Should().Contain("Nahia Urrutia", "punto de partida: quien mira sale como apoyo");

        await cut.InvokeAsync(() => cut.Find("[data-desasignarme-cabecera]").Click());

        _mediador.Enviadas.OfType<DesasignarmeDeApoyoCommand>().Should().BeEmpty("quitarse un Tenant no se deshace: pregunta antes");
        var dialogo = cut.FindComponents<DialogoConfirmacion>().Single(d => d.Instance.Visible);
        dialogo.Instance.Mensaje.Should().Contain("Talleres Norte");

        await cut.InvokeAsync(() => dialogo.Instance.OnConfirmar.InvokeAsync());

        _mediador.Enviadas.OfType<DesasignarmeDeApoyoCommand>().Should().ContainSingle()
            .Which.Should().Be(new DesasignarmeDeApoyoCommand(mio.PropuestaId));
        cut.WaitForAssertion(() => cut.FindAll("[data-gestor-cae='apoyo']").Should().BeEmpty(
            "la cabecera releyó la cartera: quien se desasignó ya no sale"));
        cut.FindAll("[data-desasignarme-cabecera]").Should().BeEmpty("ya no hay apoyo del que desasignarse");
        cut.Find("[data-gestor-cae='principal'] strong").TextContent.Trim().Should().Be("Marta Ibarra", "control: el resto del dato sigue ahí");
    }

    // ---------- Desde el Tenant propietario (decisión 2026-10-09) ----------

    /// <summary>
    /// El Administrador del Tenant propietario ve qué Operador CAE (organización) gestiona su
    /// Tenant y qué Gestor CAE (persona) es el principal, más los de apoyo. Sigue siendo solo
    /// lectura: no gana ninguna acción sobre la cartera, y el panel de acciones ni se monta.
    /// </summary>
    [Fact]
    public void Al_Administrador_del_Tenant_propietario_le_pinta_el_Operador_CAE_su_principal_y_los_de_apoyo()
    {
        var preguntasComoPropietario = 0;
        var cut = Renderizar(_ => [], rol: Roles.Administrador, comoPropietario: () =>
        {
            preguntasComoPropietario++;
            // Aunque la lectura nombrara a quien mira como principal, desde el Tenant propietario no hay acciones.
            return [OperacionDe("Prevención Levante", YoComo() with { Nombre = "Marta Ibarra", Avatar = "buho-ambar" }, Persona("Ane Larrea"))];
        });

        preguntasComoPropietario.Should().Be(1);
        var operador = cut.Find("[data-gestor-cae='operador']");
        operador.TextContent.Should().Contain("Operador CAE");
        operador.QuerySelector("strong")!.TextContent.Trim().Should().Be("Prevención Levante");

        var principal = cut.Find("[data-gestor-cae='principal']");
        principal.QuerySelector("strong")!.TextContent.Trim().Should().Be("Marta Ibarra");
        principal.QuerySelector(".cabecera-gestor-cae-pastilla")!.TextContent.Trim().Should().Be("Principal");
        principal.QuerySelectorAll(".avatar-usuario-glifo").Should().ContainSingle();
        cut.FindAll("[data-gestor-cae='apoyo']").Should().ContainSingle().Which.TextContent.Should().Contain("Ane Larrea");

        cut.Find(".cabecera-gestor-cae").GetAttribute("title")
            .Should().Be("Operador CAE que gestiona esta organización y sus Gestores CAE con Asignación de Cartera");
        cut.FindAll("button, a, input").Should().BeEmpty("leer la cartera no concede nada sobre ella");
        cut.FindComponents<PanelDarAcceso>().Should().BeEmpty("al Administrador del Tenant propietario no se le monta el panel de acciones");
        _mediador.Enviadas.OfType<ObtenerApoyosDeCarteraQuery>().Should().BeEmpty("ni se pregunta por apoyos en su nombre");
    }

    /// <summary>
    /// Con dos Operadores CAE externos, cada uno va con sus personas, y el que no tiene Gestor
    /// CAE principal lo dice junto a su nombre: no se mezclan en una lista única.
    /// </summary>
    [Fact]
    public void Al_Administrador_cada_Operador_CAE_va_con_los_suyos_y_el_que_no_tiene_principal_lo_dice()
    {
        var cut = Renderizar(_ => [], comoPropietario: () =>
        [
            OperacionDe("Gestoría Albor", Persona("Marta Ibarra")),
            OperacionDe("Prevención Levante", null)
        ]);

        cut.FindAll("[data-gestor-cae]").Select(e => (e.GetAttribute("data-gestor-cae"), e.QuerySelector("strong")?.TextContent.Trim()))
            .Should().Equal(
            [
                ("operador", "Gestoría Albor"), ("principal", "Marta Ibarra"),
                ("operador", "Prevención Levante"), ("sin-principal", null)
            ]);
    }

    /// <summary>
    /// El tope de personas no se come a un Operador CAE: con la línea llena por el primero, el segundo se
    /// nombra igual y sus personas van a «+N», cuya ventana las enseña bajo su organización.
    /// </summary>
    [Fact]
    public void Al_Administrador_un_Operador_CAE_cuyas_personas_no_caben_se_nombra_igual()
    {
        var cut = Renderizar(_ => [], comoPropietario: () =>
        [
            OperacionDe("Gestoría Albor", Persona("Marta Ibarra"), Persona("Ane Larrea")),
            OperacionDe("Prevención Levante", Persona("Iker Sola"))
        ]);

        cut.FindAll("[data-gestor-cae='operador'] strong").Select(o => o.TextContent.Trim())
            .Should().Equal("Gestoría Albor", "Prevención Levante");
        cut.Find("[data-gestor-cae-mas]").GetAttribute("data-gestor-cae-mas").Should().Be("1");
        cut.FindAll(".ventana-contexto-panel [data-gestor-cae-detalle]").Select(l => l.TextContent.Trim()).Should().Equal(
            "Operador CAE Gestoría Albor", "Marta Ibarra · Principal", "Ane Larrea · Apoyo",
            "Operador CAE Prevención Levante", "Iker Sola · Principal");
    }

    /// <summary>
    /// La misma persona puede ser principal de dos operaciones: aquí sale en las dos, cada vez
    /// bajo su Operador CAE, sin claves repetidas que maten el circuito.
    /// </summary>
    [Fact]
    public void Al_Administrador_la_misma_persona_en_dos_operaciones_sale_bajo_cada_una()
    {
        var marta = Persona("Marta Ibarra");
        var cut = Renderizar(_ => [], comoPropietario: () => [OperacionDe("Gestoría Albor", marta), OperacionDe("Gestoría Albor", marta)]);

        cut.FindAll("[data-gestor-cae='principal']").Should().HaveCount(2);
    }

    /// <summary>Quien ya recibe datos como cuenta del Operador CAE no pregunta además como propietario.</summary>
    [Fact]
    public void Si_la_lectura_del_Operador_CAE_trae_datos_no_se_hace_la_del_propietario()
    {
        var preguntasComoPropietario = 0;
        var cut = Renderizar(_ => [Operacion(Persona("Marta Ibarra"))], comoPropietario: () => { preguntasComoPropietario++; return []; });

        preguntasComoPropietario.Should().Be(0);
        cut.FindAll("[data-gestor-cae='operador']").Should().BeEmpty();
    }

    [Fact]
    public void Si_la_lectura_del_propietario_falla_lo_dice_en_vez_de_callar()
    {
        var cut = Renderizar(_ => [], comoPropietario: () => throw new InvalidOperationException("Fallo simulado."));

        cut.Find(".cabecera-gestor-cae [role=status]").TextContent.Trim().Should().Be("No pudimos cargarlo");
    }

    /// <summary>
    /// Las dos consultas salen vacías para quien no es ni una cuenta de gestión CAE del Operador
    /// CAE sobre este Tenant ni el Administrador del Tenant propietario (otro rol de ese Tenant,
    /// Soporte TALVEG), y para un Tenant de operación interna: no se pinta nada, ni el rótulo.
    /// </summary>
    [Fact]
    public void Si_las_dos_consultas_salen_vacias_no_pinta_nada()
    {
        var cut = Renderizar(_ => []);

        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Las_carteras_de_otro_Tenant_no_se_pintan_aqui()
    {
        var cut = Renderizar(_ => [new CarterasDeOperacion(Guid.NewGuid(), OtroTenant, "Otro", Persona("Marta Ibarra"), [])]);

        cut.Markup.Trim().Should().BeEmpty("la cabecera es del Tenant propietario activo");
    }

    [Fact]
    public void Sin_Tenant_activo_ni_pregunta_ni_pinta()
    {
        var preguntas = 0;
        var cut = Renderizar(_ => { preguntas++; return []; }, sinTenant: true);

        preguntas.Should().Be(0);
        cut.Markup.Trim().Should().BeEmpty();
    }

    /// <summary>Un fallo no es «nadie lo gestiona»: se dice, en vez de dejar la cabecera muda.</summary>
    [Fact]
    public void Si_la_consulta_falla_lo_dice_en_vez_de_callar()
    {
        var cut = Renderizar(_ => throw new InvalidOperationException("Fallo simulado."));

        cut.Find(".cabecera-gestor-cae [role=status]").TextContent.Trim().Should().Be("No pudimos cargarlo");
        cut.FindAll("[data-gestor-cae]").Should().BeEmpty();
        cut.FindAll("button").Should().BeEmpty("sin saber quién gestiona el Tenant no se ofrece ninguna acción");
    }
}

/// <summary>Mediador de un solo responder, para componentes que lanzan una o dos consultas.</summary>
internal sealed class MediadorPorFuncion(Func<object, object?> responder) : IMediator
{
    public List<object> Enviadas { get; } = [];

    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        Enviadas.Add(request);
        try
        {
            return Task.FromResult((TResponse)responder(request)!);
        }
        catch (Exception ex) when (ex is not InvalidCastException)
        {
            return Task.FromException<TResponse>(ex);
        }
    }

    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest
    {
        Enviadas.Add(request!);
        return Task.CompletedTask;
    }

    public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
        Task.FromResult(responder(request));

    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification => Task.CompletedTask;
}
