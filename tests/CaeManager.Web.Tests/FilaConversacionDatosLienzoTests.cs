using System.Globalization;
using Bunit;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Web.Features.Comunicaciones.Components;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Los datos de la fila del lienzo «Pulida: bandeja de la cartera» (L-12): clip de adjuntos (solo icono; el número, en su nombre accesible), insignia «Acción por aprobar», buzón en forma corta
/// y persona asignada. Todos salen de <c>ConversacionListaDto</c> tal cual los da el servidor; la fila no recalcula nada. Sin dato no se pinta nada, y en
/// pantalla no aparece «Ejecutivo» (término heredado del DTO) ni «Tenant».
/// </summary>
public class FilaConversacionDatosLienzoTests : BunitContext
{
    public FilaConversacionDatosLienzoTests() => Services.AddLogging().AddLocalization();

    [Fact]
    public void Sin_ninguno_de_los_cuatro_datos_la_fila_no_pinta_clip_insignia_buzon_ni_asignada()
    {
        var fila = Render(Crear());

        fila.FindAll(".fila-adjuntos").Should().BeEmpty();
        fila.FindAll(".fila-por-aprobar").Should().BeEmpty();
        fila.FindAll(".fila-buzon").Should().BeEmpty();
        fila.FindAll(".fila-asignada").Should().BeEmpty();
        fila.FindAll(".fila-linea-derecha").Should().BeEmpty("sin buzón, asignada ni Nº de ticket el grupo derecho no existe");
    }

    [Fact]
    public void El_clip_va_sin_numero_visible_en_la_linea_del_asunto_y_el_numero_esta_en_su_nombre_accesible()
    {
        var clip = Render(Crear() with { TotalAdjuntos = 3 }).Find(".bandeja-fila-linea2 .fila-adjuntos");

        clip.GetAttribute("role").Should().Be("img");
        clip.GetAttribute("aria-label").Should().Be("Con adjuntos: 3");
        clip.GetAttribute("title").Should().Be("Con adjuntos: 3");
        clip.TextContent.Trim().Should().BeEmpty("el lienzo pinta solo el icono, sin número");
        clip.QuerySelector(".fila-adjuntos-n").Should().BeNull();
        clip.QuerySelector("svg").Should().NotBeNull("el clip es el icono del catálogo, no un carácter");
    }

    [Fact]
    public void Con_cero_adjuntos_no_hay_clip()
    {
        Render(Crear() with { TotalAdjuntos = 0 }).FindAll(".fila-adjuntos").Should().BeEmpty();
    }

    [Fact]
    public void La_insignia_de_accion_por_aprobar_va_en_la_tercera_linea_con_texto_y_titulo_de_Vega()
    {
        var fila = Render(Crear() with { AccionPorAprobar = true });

        var insignia = fila.Find(".bandeja-fila-linea3 .fila-por-aprobar");
        insignia.TextContent.Trim().Should().Be("Acción por aprobar");
        insignia.GetAttribute("title").Should().Be("Acción por aprobar: hay una acción propuesta por Vega pendiente de aprobar");
        insignia.QuerySelector("svg").Should().NotBeNull();
    }

    [Fact]
    public void La_insignia_va_despues_del_plazo_y_antes_de_la_prioridad_como_en_el_lienzo()
    {
        var fila = Render(Crear() with { AccionPorAprobar = true, VencimientoSlaUtc = DateTime.UtcNow.AddHours(5), SlaRestante = TimeSpan.FromHours(5) });

        var piezas = fila.Find(".bandeja-fila-linea3").Children.Select(e => e.ClassList.FirstOrDefault(c => c is "plazo-conversacion" or "fila-por-aprobar" or "fila-prioridad")).Where(c => c is not null);

        piezas.Should().Equal("plazo-conversacion", "fila-por-aprobar", "fila-prioridad");
    }

    [Fact]
    public void Sin_accion_por_aprobar_no_hay_insignia()
    {
        Render(Crear() with { AccionPorAprobar = false }).FindAll(".fila-por-aprobar").Should().BeEmpty();
    }

    [Fact]
    public void El_buzon_se_enseña_con_el_nombre_de_la_conexion_y_ese_mismo_titulo()
    {
        var buzon = Render(Crear() with { BuzonNombre = "Altas y bajas" }).Find(".bandeja-fila-linea3 .fila-buzon");

        buzon.TextContent.Trim().Should().Be("Altas y bajas");
        buzon.GetAttribute("title").Should().Be("Altas y bajas");
    }

    [Fact]
    public void El_buzon_personal_se_enseña_como_Mi_buzon_personal_y_no_con_su_direccion()
    {
        var fila = Render(Crear() with { BuzonNombre = "lucia@operador-ejemplo.test", BuzonEsPersonal = true });

        var buzon = fila.Find(".fila-buzon");
        buzon.TextContent.Trim().Should().Be("Mi buzón personal");
        buzon.GetAttribute("title").Should().Be("Mi buzón personal");
        fila.Markup.Should().NotContain("lucia@operador-ejemplo.test");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sin_nombre_de_buzon_y_sin_ser_personal_no_se_pinta_buzon(string? nombre)
    {
        Render(Crear() with { BuzonNombre = nombre, BuzonEsPersonal = false }).FindAll(".fila-buzon").Should().BeEmpty();
    }

    [Theory]
    [InlineData("Marta Gil Ríos", "MG")]
    [InlineData("marta gil", "MG")]
    [InlineData("123 Marta Gil", "MG")]
    [InlineData("Marta", "M")]
    [InlineData("  Marta   Gil  ", "MG")]
    // Los cuatro siguientes distinguen el helper de la persona usuaria del de Empresa/Tenant (AvatarTenant.Iniciales, que parte solo por espacios y descarta
    // la palabra que no empieza por letra): ese daría «AR», «M», «M» y «G».
    [InlineData("Ana-Belén Ruiz", "AB")]
    [InlineData("marta.gil@operador.test", "MG")]
    [InlineData("Marta 2Gil", "MG")]
    [InlineData("(Marta) Gil", "MG")]
    public void La_persona_asignada_se_enseña_con_sus_iniciales_y_su_nombre_en_el_titulo(string nombre, string iniciales)
    {
        var asignada = Render(Crear() with { EjecutivoAsignadoNombre = nombre }).Find(".bandeja-fila-linea3 .fila-asignada");

        asignada.TextContent.Trim().Should().Be(iniciales);
        asignada.GetAttribute("title").Should().Be($"Asignada a {nombre.Trim()}");
        asignada.GetAttribute("aria-label").Should().Be($"Asignada a {nombre.Trim()}");
        asignada.QuerySelector("span.avatar-usuario").Should().NotBeNull("la persona la pinta el componente AvatarUsuario, no un texto suelto de la fila");
    }

    [Fact]
    public void La_persona_asignada_con_avatar_elegido_pinta_su_emoji_con_AvatarUsuario_pequeno_y_no_las_iniciales()
    {
        var asignada = Render(Crear() with { EjecutivoAsignadoNombre = "Marta Gil", EjecutivoAsignadoAvatar = "buho-ambar" }).Find(".bandeja-fila-linea3 .fila-asignada");

        var avatar = asignada.QuerySelector("span.avatar-usuario")!;
        avatar.ClassList.Should().Contain(["avatar-usuario-pequeno", "avatar-usuario-emoji", "avatar-usuario-tono-ambar"]);
        avatar.GetAttribute("aria-hidden").Should().Be("true", "el avatar es decorativo: el nombre lo lleva el envoltorio");
        avatar.QuerySelector(".avatar-usuario-glifo")!.TextContent.Should().Be("🦉");
        asignada.TextContent.Trim().Should().Be("🦉").And.NotContain("MG");
        asignada.GetAttribute("role").Should().Be("img");
        asignada.GetAttribute("aria-label").Should().Be("Asignada a Marta Gil");
        asignada.GetAttribute("title").Should().Be("Asignada a Marta Gil");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dragon-verde")] // una clave fuera del catálogo (motivo retirado, dato corrupto) cae a las iniciales, no a un hueco
    public void La_persona_asignada_sin_avatar_elegido_pinta_las_iniciales_del_propio_componente(string? clave)
    {
        var asignada = Render(Crear() with { EjecutivoAsignadoNombre = "Marta Gil", EjecutivoAsignadoAvatar = clave }).Find(".bandeja-fila-linea3 .fila-asignada");

        var avatar = asignada.QuerySelector("span.avatar-usuario")!;
        avatar.ClassList.Should().Contain("avatar-usuario-pequeno").And.NotContain(c => c.StartsWith("avatar-usuario-tono-") || c == "avatar-usuario-emoji");
        avatar.TextContent.Trim().Should().Be("MG");
        asignada.QuerySelector(".avatar-usuario-glifo").Should().BeNull();
        asignada.GetAttribute("title").Should().Be("Asignada a Marta Gil");
    }

    [Fact]
    public void La_fila_sin_asignar_no_pinta_avatar_aunque_llegue_una_clave()
    {
        // Sin persona asignada visible no hay avatar: ni vacío, ni el de otra persona, ni «Sin asignar».
        var fila = Render(Crear() with { EjecutivoAsignadoId = null, EjecutivoAsignadoNombre = null, EjecutivoAsignadoAvatar = "buho-ambar" });

        fila.FindAll(".fila-asignada").Should().BeEmpty();
        fila.FindAll(".bandeja-fila-linea3 .avatar-usuario").Should().BeEmpty();
        fila.FindAll(".avatar-usuario-glifo").Should().BeEmpty();
        fila.Markup.Should().NotContain("🦉").And.NotContain("Sin asignar");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("123 456")]
    public void Si_el_nombre_de_la_persona_asignada_no_llega_la_fila_no_enseña_ningun_nombre(string? nombre)
    {
        // El directorio no la deja ver desde el Tenant activo: el DTO trae el id pero no el nombre. Ni iniciales, ni «Sin asignar», ni un «Asignada a» vacío.
        var fila = Render(Crear() with { EjecutivoAsignadoId = Guid.NewGuid(), EjecutivoAsignadoNombre = nombre });

        fila.FindAll(".fila-asignada").Should().BeEmpty();
        fila.Markup.Should().NotContain("Asignada").And.NotContain("Sin asignar");
    }

    [Fact]
    public void El_grupo_derecho_ordena_buzon_asignada_y_ticket()
    {
        var fila = Render(Crear() with { BuzonNombre = "Altas y bajas", EjecutivoAsignadoNombre = "Marta Gil", NumeroTicket = 10479 });

        var grupo = fila.Find(".bandeja-fila-linea3 > .fila-linea-derecha");
        grupo.Children.Select(e => e.ClassList.First()).Should().Equal("fila-buzon", "fila-asignada", "fila-ticket");
        grupo.QuerySelector(".fila-ticket")!.TextContent.Trim().Should().Be("Nº 10479");
    }

    [Fact]
    public void Con_solo_ticket_el_grupo_derecho_lo_lleva_solo()
    {
        var grupo = Render(Crear() with { NumeroTicket = 7 }).Find(".fila-linea-derecha");

        grupo.Children.Should().ContainSingle().Which.ClassList.Should().Contain("fila-ticket");
    }

    [Fact]
    public void Con_todos_los_datos_no_aparece_Ejecutivo_ni_Tenant_ni_valores_en_crudo()
    {
        var fila = Render(Crear() with
        {
            TotalAdjuntos = 2,
            AccionPorAprobar = true,
            BuzonNombre = "Altas y bajas",
            EjecutivoAsignadoNombre = "Marta Gil",
            NumeroTicket = 5,
        });

        fila.Markup.Should().NotContainAny("Ejecutivo", "Tenant", "BuzonEsPersonal", "TotalAdjuntos", "AccionPorAprobar");
    }

    [Fact]
    public void Los_cuatro_datos_conviven_con_lo_que_ya_tenia_la_fila()
    {
        var fila = Render(Crear() with
        {
            TotalAdjuntos = 2,
            AccionPorAprobar = true,
            BuzonNombre = "Altas y bajas",
            EjecutivoAsignadoNombre = "Marta Gil",
            NumeroTicket = 5,
            MensajesRuido = 2,
            Prioridad = PrioridadConversacion.Urgente,
            VencimientoSlaUtc = DateTime.UtcNow.AddHours(1),
            SlaRestante = TimeSpan.FromHours(1),
        });

        // Lo que ya había: remitente, asunto, plazo, prioridad, estado, ruido, ticket y vista previa.
        fila.Find(".bandeja-fila-remitente").TextContent.Should().Be("contacto@ejemplo.test");
        fila.Find(".bandeja-fila-asunto").TextContent.Should().Be("Asunto");
        fila.Find(".plazo-conversacion").TextContent.Trim().Should().StartWith("Por vencer");
        fila.Find(".fila-prioridad").TextContent.Trim().Should().Be("Urgente");
        fila.Find(".bandeja-fila-linea3 .badge").TextContent.Trim().Should().Be("Abierta");
        fila.Markup.Should().Contain("2 mensajes de ruido");
        fila.Find(".fila-ticket").TextContent.Trim().Should().Be("Nº 5");
        fila.Find(".fila-vista-previa").TextContent.Should().Be("Texto");
    }

    [Theory]
    [InlineData("es-ES", "Con adjuntos: 2", "Acción por aprobar", "Mi buzón personal", "Asignada a Marta Gil")]
    [InlineData("ca-ES", "Amb adjunts: 2", "Acció per aprovar", "La meva bústia personal", "Assignada a Marta Gil")]
    public void Los_textos_de_los_cuatro_datos_estan_localizados(string idioma, string adjuntos, string porAprobar, string buzonPersonal, string asignada)
    {
        var anterior = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(idioma);
            var fila = Render(Crear() with
            {
                TotalAdjuntos = 2,
                AccionPorAprobar = true,
                BuzonNombre = "x",
                BuzonEsPersonal = true,
                EjecutivoAsignadoNombre = "Marta Gil",
            });

            fila.Find(".fila-adjuntos").GetAttribute("aria-label").Should().Be(adjuntos);
            fila.Find(".fila-por-aprobar-tx").TextContent.Trim().Should().Be(porAprobar);
            fila.Find(".fila-buzon").TextContent.Trim().Should().Be(buzonPersonal);
            fila.Find(".fila-asignada").GetAttribute("title").Should().Be(asignada);
        }
        finally
        {
            CultureInfo.CurrentUICulture = anterior;
        }
    }

    [Fact]
    public void La_hoja_de_estilos_acota_el_grupo_derecho_y_pinta_la_insignia_opaca_sobre_la_superficie()
    {
        // bUnit no calcula CSS: se inspecciona la hoja. Sin max-width y min-width el buzón largo desborda la fila (el flex-shrink: 0 de Bandeja.razor.css gana al del grupo);
        // sin el fondo opaco el tinte translúcido deja la insignia bajo 4,5:1 sobre la fila activa y en hover.
        var css = LeerHoja();

        Regla(css, @"li\.fila-conversacion \.fila-linea-derecha").Should().MatchRegex(@"max-width\s*:\s*100%").And.MatchRegex(@"min-width\s*:\s*0");
        Regla(css, @"\.fila-buzon").Should().MatchRegex(@"min-width\s*:\s*0").And.MatchRegex(@"text-overflow\s*:\s*ellipsis").And.MatchRegex(@"max-width\s*:\s*var\(--space-24\)");
        Regla(css, @"\.fila-por-aprobar").Should().MatchRegex(@"background\s*:[^;]*var\(--color-system-tint\)[^;]*var\(--color-surface\)");
        Regla(css, @"\.fila-por-aprobar ::deep \.icono").Should().MatchRegex(@"width\s*:\s*12px");
    }

    private static string Regla(string css, string selector)
    {
        var m = System.Text.RegularExpressions.Regex.Match(css, @"(?m)^" + selector + @"\s*\{(?<c>[^}]*)\}");
        m.Success.Should().BeTrue($"la regla «{selector}» debe existir");
        return m.Groups["c"].Value;
    }

    private static string LeerHoja()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio para leer la hoja de estilos");
        return File.ReadAllText(Path.Combine(dir!, "src", "CaeManager.Web", "Features", "Comunicaciones", "Components", "FilaConversacion.razor.css"));
    }

    private IRenderedComponent<FilaConversacion> Render(ConversacionListaDto conversacion) =>
        Render<FilaConversacion>(p => p.Add(c => c.Conversacion, conversacion));

    private static ConversacionListaDto Crear() => new(
        Guid.NewGuid(), null, null, "Asunto", EstadoConversacion.Abierta, null, "contacto@ejemplo.test", "Texto", DateTime.UtcNow, 1,
        CanalConversacion.Correo, null, DireccionMensaje.Entrante);
}
