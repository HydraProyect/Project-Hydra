using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands;
using CaeManager.Application.Documentos.Commands.ConfirmarVigenciaAcreditacion;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionAceptada;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionRechazada;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionSubida;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

using PaginaDocumentos = CaeManager.Web.Features.Documentos.Pages.Documentos;

/// <summary>
/// D-18: el menú «Acreditación ▾» en cada fila del listado agrupado por Plataforma CAE. La pantalla solo dibuja el menú, lo
/// despacha a los diálogos y a los Commands de siempre y decide a quién se enseña (D-3); aquí la consulta es un doble.
/// </summary>
public partial class DocumentosGen2Tests
{
    private static IElement FilaDe(IRenderedComponent<PaginaDocumentos> cut, string propietario) =>
        cut.FindAll(".documentos-tabla-visible tbody tr:has(button.enlace-nombre-fila)")
            .First(f => f.TextContent.Contains(propietario));

    private static EscenarioVinculos ConPropietarios(EscenarioVinculos e)
    {
        var original = e.Mediador.Documentos;
        e.Mediador.Documentos = q => original(q).Select(d => d with { PropietarioId = Guid.NewGuid() }).ToList();
        return e;
    }

    [Theory]
    [InlineData("Administrador", true)]
    [InlineData("GestorCae", true)]
    [InlineData("Consulta", false)]
    [InlineData("Cliente", false)]
    public async Task Por_Plataforma_CAE_cada_fila_acreditada_lleva_el_menu_Acreditacion_solo_con_escritura(string rol, bool escritura)
    {
        var e = ConPropietarios(ConVinculos());
        var (cut, _) = Renderizar(e.Mediador, rol: rol);
        cut.WaitForAssertion(() => cut.Find("tbody button.enlace-nombre-fila"));

        await Agrupar(cut, "Por Plataforma CAE");

        cut.WaitForAssertion(() => cut.FindAll("tr.documentos-cabecera-grupo-vinculo").Should().NotBeEmpty());
        var filas = cut.FindAll(".documentos-tabla-visible tbody tr:has(button.enlace-nombre-fila)");
        var conMenu = filas.Where(f => f.QuerySelector(".menu-acreditacion") is not null).ToList();
        // Cuatro filas acreditadas (dos de Nalanda en el mismo Documento, la rechazada y la pendiente de Ctaima) y una sin vínculo.
        conMenu.Should().HaveCount(escritura ? 4 : 0);
        // Artboard Agrupar-plataforma: la fila acreditada lleva «Acreditación ▾» y ya no el botón suelto «Gestionar»; la fila sin
        // vínculo (sin menú que lo recoja) lo conserva, y «Ver» y el 360 siguen en todas.
        foreach (var fila in filas)
        {
            var acreditada = fila.QuerySelector(".menu-acreditacion") is not null;
            fila.QuerySelectorAll(".documentos-accion-rapida").Should().HaveCount(escritura && !acreditada ? 1 : 0);
            fila.QuerySelectorAll("button").Should().Contain(b => b.TextContent.Trim() == "Ver");
        }
        if (escritura)
        {
            // El grupo «sin vínculo» nace plegado: se abre para ver su fila.
            var cabeceraSin = CabeceraDeVinculo(cut, ObtenerDocumentosQuery.GrupoSin).QuerySelector<IElement>("button.grupo-lista-cabecera")!;
            if (cabeceraSin.GetAttribute("aria-expanded") == "false")
                await cabeceraSin.ClickAsync(new MouseEventArgs());
            filas = cut.FindAll(".documentos-tabla-visible tbody tr:has(button.enlace-nombre-fila)");
            filas.Where(f => f.QuerySelector(".menu-acreditacion") is null).Should().ContainSingle()
                .Which.QuerySelectorAll(".documentos-accion-rapida").Should().ContainSingle("la fila sin vínculo no tiene menú y conserva «Gestionar»");
        }
    }

    [Theory]
    [InlineData("Por Cliente")]
    [InlineData("Sin agrupar")]
    public async Task Fuera_del_agrupado_por_Plataforma_CAE_las_filas_no_ganan_el_menu(string agrupacion)
    {
        var e = ConPropietarios(ConVinculos());
        var (cut, _) = Renderizar(e.Mediador);
        cut.WaitForAssertion(() => cut.Find("tbody button.enlace-nombre-fila"));

        await Agrupar(cut, agrupacion);

        cut.WaitForAssertion(() => cut.FindAll(".documentos-tabla-visible tbody button.enlace-nombre-fila").Should().NotBeEmpty());
        cut.FindAll(".menu-acreditacion").Should().BeEmpty();
    }

    [Fact]
    public async Task Cada_fila_ofrece_las_opciones_de_su_estado_con_el_nombre_de_su_plataforma()
    {
        var e = ConPropietarios(ConVinculos());
        var (cut, _) = Renderizar(e.Mediador);
        cut.WaitForAssertion(() => cut.Find("tbody button.enlace-nombre-fila"));
        await Agrupar(cut, "Por Plataforma CAE");
        cut.WaitForAssertion(() => cut.FindAll("tr.documentos-cabecera-grupo-vinculo").Should().NotBeEmpty());

        var rechazada = FilaDe(cut, "Ana Ruiz Pardo");
        await rechazada.QuerySelector<IElement>("button.menu-acciones-disparador")!.ClickAsync(new MouseEventArgs());
        cut.Find("[role=menu] .menu-acciones-titulo").TextContent.Trim().Should().Be("Acreditación en Nalanda");
        cut.FindAll("[role=menu] [role=menuitem]").Select(o => o.TextContent.Trim()).Should().Equal(
            "Subir versión corregida", "Marcar validado", "Marcar rechazado…", "Gestionar acreditación…", "Abrir portal ↗");
        cut.Find("[role=menu] a[role=menuitem]").GetAttribute("href").Should().StartWith("https://nalanda.example.com");
    }

    [Fact]
    public async Task Marcar_subido_despacha_el_comando_de_siempre_con_la_acreditacion_de_la_fila()
    {
        var e = ConPropietarios(ConVinculos());
        var enviados = new List<MarcarAcreditacionSubidaCommand>();
        e.Mediador.Interceptar = p => p is MarcarAcreditacionSubidaCommand c
            ? Task.FromResult<object?>(Registrar(enviados, c))
            : null;
        var (cut, mediador) = Renderizar(e.Mediador);
        cut.WaitForAssertion(() => cut.Find("tbody button.enlace-nombre-fila"));
        await Agrupar(cut, "Por Plataforma CAE");
        cut.WaitForAssertion(() => cut.FindAll("tr.documentos-cabecera-grupo-vinculo").Should().NotBeEmpty());
        var pendiente = e.Mediador.Documentos(UltimaConsultaDeDocumentos(mediador))
            .Single(d => d.Grupo?.Acreditacion?.Estado == EstadoAcreditacion.PendienteDeSubir).Grupo!.Acreditacion!.Id;

        await FilaDe(cut, "Luis Mora Gil").QuerySelector<IElement>("button.menu-acciones-disparador")!.ClickAsync(new MouseEventArgs());
        await cut.FindAll("[role=menu] [role=menuitem]").Single(o => o.TextContent.Trim() == "Marcar subido").ClickAsync(new MouseEventArgs());

        enviados.Should().ContainSingle().Which.AcreditacionId.Should().Be(pendiente);
    }

    // ------------------------------------------------------------- «Abrir portal» por fila (la dirección de SU canal)

    private const string DireccionA = "https://portal-a.example.com/acceso";
    private const string DireccionB = "https://portal-b.example.com/acceso";

    /// <summary>
    /// Una fila de Nalanda por cada tupla, cada una con su propietario, su estado y la dirección de su canal. La cabecera del grupo
    /// va SIN dirección (con canales de direcciones distintas no hay un portal único): lo que abre cada fila sale de la fila.
    /// </summary>
    private static MediadorControlado EnNalanda(params (string Propietario, EstadoAcreditacion Estado, string? Url, Guid Id)[] filas)
    {
        var documentos = filas.Select(f => EnPlataforma(
            DocumentoDeFila() with { Id = Guid.NewGuid(), PropietarioId = Guid.NewGuid(), PropietarioNombre = f.Propietario },
            NalandaId, f.Id, f.Estado, "Cliente Norte", urlCanal: f.Url)).ToList();
        return new MediadorControlado
        {
            Documentos = q => q.AgruparPor == AgrupacionDocumentos.PlataformaCae
                ? documentos
                : documentos.Select(d => d with { Grupo = null }).ToList(),
            ResumenesDeGrupo = q => q.AgruparPor == AgrupacionDocumentos.PlataformaCae
                ? new Dictionary<string, ResumenDeGrupo> { [NalandaId.ToString()] = ResumenPlataforma("Nalanda", filas.Length, null) }
                : null,
        };
    }

    private async Task<IRenderedComponent<PaginaDocumentos>> AgruparPorPlataformaAsync(MediadorControlado mediador, string rol = "GestorCae")
    {
        var (cut, _) = Renderizar(mediador, rol: rol);
        cut.WaitForAssertion(() => cut.Find("tbody button.enlace-nombre-fila"));
        await Agrupar(cut, "Por Plataforma CAE");
        cut.WaitForAssertion(() => cut.FindAll("tr.documentos-cabecera-grupo-vinculo").Should().NotBeEmpty());
        return cut;
    }

    private static async Task AbrirMenuAsync(IRenderedComponent<PaginaDocumentos> cut, string propietario) =>
        await FilaDe(cut, propietario).QuerySelector<IElement>("button.menu-acciones-disparador")!.ClickAsync(new MouseEventArgs());

    private static async Task ElegirOpcionAsync(IRenderedComponent<PaginaDocumentos> cut, string propietario, string opcion)
    {
        await AbrirMenuAsync(cut, propietario);
        await cut.FindAll("[role=menu] [role=menuitem]").Single(o => o.TextContent.Trim() == opcion).ClickAsync(new MouseEventArgs());
    }

    private static List<string> OpcionesDe(IRenderedComponent<PaginaDocumentos> cut, string propietario) =>
        FilaDe(cut, propietario).QuerySelectorAll("[role=menu] [role=menuitem]").Select(o => o.TextContent.Trim()).ToList();

    [Fact]
    public async Task Dos_canales_con_direcciones_distintas_en_un_mismo_grupo_abren_cada_uno_la_suya()
    {
        var cut = await AgruparPorPlataformaAsync(EnNalanda(
            ("Ana Ruiz Pardo", EstadoAcreditacion.Rechazada, DireccionA, Guid.NewGuid()),
            ("Luis Mora Gil", EstadoAcreditacion.PendienteDeSubir, DireccionB, Guid.NewGuid())));
        CabeceraDeVinculo(cut, NalandaId.ToString()).QuerySelector(".documentos-abrir-portal").Should().BeNull(
            "control positivo: con dos direcciones distintas la cabecera del grupo no ofrece un portal único");

        await AbrirMenuAsync(cut, "Ana Ruiz Pardo");
        await AbrirMenuAsync(cut, "Luis Mora Gil");

        FilaDe(cut, "Ana Ruiz Pardo").QuerySelector("[role=menu] a[role=menuitem]")!.GetAttribute("href").Should().Be(DireccionA);
        FilaDe(cut, "Luis Mora Gil").QuerySelector("[role=menu] a[role=menuitem]")!.GetAttribute("href").Should().Be(DireccionB);
        FilaDe(cut, "Ana Ruiz Pardo").QuerySelector("[role=menu] a[role=menuitem]")!.GetAttribute("target").Should().Be("_blank");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("javascript:alert(1)")]
    public async Task Un_canal_sin_direccion_valida_no_ofrece_abrir_portal_pero_si_el_resto_de_la_fila(string? url)
    {
        var cut = await AgruparPorPlataformaAsync(EnNalanda(
            ("Ana Ruiz Pardo", EstadoAcreditacion.Rechazada, url, Guid.NewGuid()),
            ("Luis Mora Gil", EstadoAcreditacion.PendienteDeSubir, DireccionB, Guid.NewGuid())));

        await AbrirMenuAsync(cut, "Ana Ruiz Pardo");
        await AbrirMenuAsync(cut, "Luis Mora Gil");

        OpcionesDe(cut, "Ana Ruiz Pardo").Should().Equal(
            "Subir versión corregida", "Marcar validado", "Marcar rechazado…", "Gestionar acreditación…");
        OpcionesDe(cut, "Luis Mora Gil").Should().EndWith("Abrir portal ↗", "control positivo: la fila con dirección sí lo ofrece");
    }

    [Theory]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    public async Task Un_rol_sin_acceso_al_portal_no_ve_el_menu_ni_la_direccion_aunque_llegue_en_la_fila(string rol)
    {
        var cut = await AgruparPorPlataformaAsync(EnNalanda(
            ("Ana Ruiz Pardo", EstadoAcreditacion.Rechazada, DireccionA, Guid.NewGuid())), rol);

        cut.FindAll(".menu-acreditacion").Should().BeEmpty();
        cut.Markup.Should().NotContain("Abrir portal").And.NotContain(DireccionA);
    }

    // ------------------------------------------------------------- despacho de cada opción del menú

    [Fact]
    public async Task Marcar_validado_pregunta_la_vigencia_y_despacha_el_comando_con_la_acreditacion_de_la_fila()
    {
        var rechazada = Guid.NewGuid();
        var mediador = EnNalanda(("Ana Ruiz Pardo", EstadoAcreditacion.Rechazada, null, rechazada));
        var enviados = new List<MarcarAcreditacionAceptadaCommand>();
        mediador.Interceptar = p => p is MarcarAcreditacionAceptadaCommand c
            ? Task.FromResult<object?>(Result.Exito(new ResultadoAnotacionAcreditacionDto(
                Registrar(enviados, c).AcreditacionId, EstadoAcreditacion.Aceptada, c.Vigencia, Guid.NewGuid())))
            : null;
        var cut = await AgruparPorPlataformaAsync(mediador);

        await ElegirOpcionAsync(cut, "Ana Ruiz Pardo", "Marcar validado");

        var dialogo = cut.Find("[role=dialog]");
        dialogo.TextContent.Should().Contain("Marcar como validado");
        enviados.Should().BeEmpty("abrir el diálogo no valida nada");
        await dialogo.QuerySelector("select")!.ChangeAsync(new ChangeEventArgs { Value = nameof(EstadoVigenciaEnPlataforma.NoVenceAqui) });
        var consultasAntes = mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Count();
        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Marcar validado").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => enviados.Should().ContainSingle());
        enviados.Single().AcreditacionId.Should().Be(rechazada);
        enviados.Single().Vigencia.Should().Be(VigenciaEnPlataforma.NoVenceAqui);
        cut.WaitForAssertion(() => mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Count().Should().BeGreaterThan(consultasAntes,
            "tras validar, el listado se recarga"));
        Toasts().Should().Contain(t => t.Mensaje.Contains("no caduca") && t.TextoAccion == "Deshacer");
    }

    [Fact]
    public async Task Marcar_rechazado_exige_el_detalle_y_despacha_el_comando_con_la_causa_elegida()
    {
        var pendiente = Guid.NewGuid();
        var mediador = EnNalanda(("Luis Mora Gil", EstadoAcreditacion.PendienteDeSubir, null, pendiente));
        var enviados = new List<MarcarAcreditacionRechazadaCommand>();
        mediador.Interceptar = p => p is MarcarAcreditacionRechazadaCommand c
            ? Task.FromResult<object?>(RegistrarRechazo(enviados, c))
            : null;
        var cut = await AgruparPorPlataformaAsync(mediador);
        var causa = Enum.GetValues<CausaRechazoAcreditacion>().First(c => c != CausaRechazoAcreditacion.Ilegible);

        await ElegirOpcionAsync(cut, "Luis Mora Gil", "Marcar rechazado…");
        cut.Find("[role=dialog]").TextContent.Should().Contain("Marcar como rechazado");
        // Artboard Listado-dialogos: bajo el título, la línea atenuada «<Plataforma CAE> · <tipo> de <propietario>», y la causa y el
        // detalle como hijos de un contenedor que los separa 12 px.
        var contexto = cut.Find("[role=dialog] p.rechazo-contexto").TextContent.Trim();
        contexto.Should().StartWith("Nalanda · ").And.EndWith(" de Luis Mora Gil");
        var campos = cut.Find("[role=dialog] .rechazo-campos");
        campos.QuerySelector("select").Should().NotBeNull();
        campos.QuerySelector("textarea").Should().NotBeNull();

        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Marcar rechazado").ClickAsync(new MouseEventArgs());
        enviados.Should().BeEmpty("sin detalle no se manda nada");
        cut.Find("[role=dialog]").TextContent.Should().Contain("Describe el motivo del rechazo.");

        await cut.Find("[role=dialog] select").ChangeAsync(new ChangeEventArgs { Value = causa.ToString() });
        await cut.Find("[role=dialog] textarea").InputAsync(new ChangeEventArgs { Value = "Firma del representante ilegible" });
        var consultasAntes = mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Count();
        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Marcar rechazado").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => enviados.Should().ContainSingle());
        enviados.Single().Should().Be(new MarcarAcreditacionRechazadaCommand(pendiente, causa, "Firma del representante ilegible"));
        cut.WaitForAssertion(() => mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Count().Should().BeGreaterThan(consultasAntes));
        cut.FindAll("[role=dialog]").Should().BeEmpty("el diálogo se cierra al guardar");
    }

    [Fact]
    public async Task Anotar_vigencia_de_una_fila_abre_el_dialogo_y_anota_solo_la_acreditacion_de_esa_fila()
    {
        var elegida = Guid.NewGuid();
        var otra = Guid.NewGuid();
        var mediador = EnNalanda(
            ("Ana Ruiz Pardo", EstadoAcreditacion.Aceptada, null, elegida),
            ("Luis Mora Gil", EstadoAcreditacion.Aceptada, null, otra));
        var anotadas = new List<ConfirmarVigenciaAcreditacionCommand>();
        mediador.Interceptar = p => p is ConfirmarVigenciaAcreditacionCommand c
            ? Task.FromResult<object?>(Anotar(anotadas, c))
            : null;
        var cut = await AgruparPorPlataformaAsync(mediador);

        await ElegirOpcionAsync(cut, "Ana Ruiz Pardo", "Anotar vigencia…");

        var dialogo = cut.Find("[role=dialog]");
        dialogo.TextContent.Should().Contain("Anotar vigencia");
        // Artboard Listado-dialogos: una sola acreditación, en singular, con la Plataforma CAE y sin «1 de 1»; bajo los campos.
        dialogo.QuerySelector(".anotar-vigencia-ayuda")!.TextContent.Trim()
            .Should().Be("Se anotará la misma vigencia en 1 acreditación validada en Nalanda.");
        dialogo.TextContent.Should().NotContain("1 de 1").And.NotContain("acreditaciones validadas");
        dialogo.QuerySelector(".campo-fecha-fila input[inputmode=numeric]").Should().NotBeNull(
            "el campo de la fecha sigue siendo el CampoTexto Type=\"date\" (en pantalla, dd/mm/aaaa con calendario), no un campo de texto suelto");
        anotadas.Should().BeEmpty("abrir no anota");
        await dialogo.QuerySelector("select")!.ChangeAsync(new ChangeEventArgs { Value = nameof(EstadoVigenciaEnPlataforma.NoVenceAqui) });
        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Guardar vigencia").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => anotadas.Should().ContainSingle());
        anotadas.Single().AcreditacionId.Should().Be(elegida, "la otra fila del grupo no se toca");
        anotadas.Single().Vigencia.Should().Be(VigenciaEnPlataforma.NoVenceAqui);
    }

    [Fact]
    public async Task Gestionar_acreditacion_abre_el_panel_de_la_fila_y_no_se_ofrece_a_un_vehiculo()
    {
        var mediador = EnNalanda(("Ana Ruiz Pardo", EstadoAcreditacion.Rechazada, null, Guid.NewGuid()),
            ("Luis Mora Gil", EstadoAcreditacion.Rechazada, null, Guid.NewGuid()));
        var documentos = mediador.Documentos;
        mediador.Documentos = q => documentos(q).Select(d => d.PropietarioNombre == "Luis Mora Gil" ? d with { Ambito = AmbitoAplicacion.Vehiculo } : d).ToList();
        var cut = await AgruparPorPlataformaAsync(mediador);

        await AbrirMenuAsync(cut, "Luis Mora Gil");
        OpcionesDe(cut, "Luis Mora Gil").Should().Contain("Marcar validado", "control positivo: el menú de la fila de un Vehículo existe y tiene opciones")
            .And.NotContain("Gestionar acreditación…", "el panel enlaza con la ficha 360 de una Empresa o un Trabajador, no de un Vehículo");

        await ElegirOpcionAsync(cut, "Ana Ruiz Pardo", "Gestionar acreditación…");

        cut.WaitForAssertion(() => cut.Find("[role=dialog]").TextContent.Should().Contain("Gestionar acreditación en plataforma")
            .And.Contain("Ana Ruiz Pardo", "el panel es el de la fila elegida").And.NotContain("Luis Mora Gil"));
    }

    [Fact]
    public async Task Subir_version_corregida_abre_el_drawer_del_Documento_de_la_fila_elegida()
    {
        var mediador = EnNalanda(("Ana Ruiz Pardo", EstadoAcreditacion.Rechazada, null, Guid.NewGuid()),
            ("Luis Mora Gil", EstadoAcreditacion.Rechazada, null, Guid.NewGuid()));
        mediador.Interceptar = p => p is ObtenerDocumentoPorIdQuery ? Task.FromResult<object?>(null) : null;
        var cut = await AgruparPorPlataformaAsync(mediador);
        var luis = mediador.Documentos(mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Last()).Single(d => d.PropietarioNombre == "Luis Mora Gil").Id;

        await ElegirOpcionAsync(cut, "Luis Mora Gil", "Subir versión corregida");

        cut.WaitForAssertion(() => mediador.Enviadas.OfType<ObtenerDocumentoPorIdQuery>().Should().Equal(
            [new ObtenerDocumentoPorIdQuery(luis)], "abre el Documento de la fila elegida y no otro"));
    }

    [Fact]
    public async Task Una_acreditacion_No_exigida_sin_ninguna_opcion_no_pinta_un_menu_vacio_y_conserva_Gestionar()
    {
        var mediador = EnNalanda(("Ana Ruiz Pardo", EstadoAcreditacion.NoRequerida, null, Guid.NewGuid()),
            ("Luis Mora Gil", EstadoAcreditacion.NoRequerida, null, Guid.NewGuid()));
        var documentos = mediador.Documentos;
        mediador.Documentos = q => documentos(q).Select(d => d.PropietarioNombre == "Luis Mora Gil" ? d with { Ambito = AmbitoAplicacion.Vehiculo } : d).ToList();
        var cut = await AgruparPorPlataformaAsync(mediador);

        // Un Trabajador sí tiene «Gestionar acreditación…»: el menú existe con esa única opción.
        await AbrirMenuAsync(cut, "Ana Ruiz Pardo");
        OpcionesDe(cut, "Ana Ruiz Pardo").Should().Equal("Gestionar acreditación…");
        FilaDe(cut, "Ana Ruiz Pardo").QuerySelectorAll(".documentos-accion-rapida").Should().BeEmpty();
        // Un Vehículo no tiene ninguna: sin menú, y el botón de siempre para no quedarse sin acción.
        var luis = FilaDe(cut, "Luis Mora Gil");
        luis.QuerySelector(".menu-acreditacion").Should().BeNull();
        luis.QuerySelectorAll(".documentos-accion-rapida").Should().ContainSingle().Which.TextContent.Trim().Should().Be("Gestionar");
    }

    // ------------------------------------------------------------- el servidor rechaza: ModalesAcreditacion lo cuenta

    [Fact]
    public async Task Si_el_servidor_rechaza_marcar_validado_el_dialogo_sigue_abierto_con_el_mensaje_y_no_recarga()
    {
        var mediador = EnNalanda(("Ana Ruiz Pardo", EstadoAcreditacion.Rechazada, null, Guid.NewGuid()));
        mediador.Interceptar = p => p is MarcarAcreditacionAceptadaCommand
            ? Task.FromResult<object?>(Result.Fallo<ResultadoAnotacionAcreditacionDto>(
                Error.Crear("Acreditacion.CambioConcurrente", "Alguien cambió esta acreditación mientras la editabas.")))
            : null;
        var cut = await AgruparPorPlataformaAsync(mediador);
        await ElegirOpcionAsync(cut, "Ana Ruiz Pardo", "Marcar validado");
        await cut.Find("[role=dialog] select").ChangeAsync(new ChangeEventArgs { Value = nameof(EstadoVigenciaEnPlataforma.NoVenceAqui) });
        var consultasAntes = mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Count();

        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Marcar validado").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Find("[role=dialog]").TextContent.Should().Contain("Alguien cambió esta acreditación mientras la editabas."));
        mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Count().Should().Be(consultasAntes, "un fallo no recarga el listado");
        Toasts().Should().NotContain(t => t.Tono == TonoToast.Exito, "no hay aviso de éxito si el servidor lo rechazó");
    }

    [Fact]
    public async Task Si_el_servidor_rechaza_marcar_rechazado_el_dialogo_muestra_el_mensaje_y_conserva_lo_escrito()
    {
        var mediador = EnNalanda(("Luis Mora Gil", EstadoAcreditacion.PendienteDeSubir, null, Guid.NewGuid()));
        mediador.Interceptar = p => p is MarcarAcreditacionRechazadaCommand
            ? Task.FromResult<object?>(Result.Fallo(Error.Crear("Acreditacion.NoEditable", "Esta acreditación ya no admite el cambio.")))
            : null;
        var cut = await AgruparPorPlataformaAsync(mediador);
        await ElegirOpcionAsync(cut, "Luis Mora Gil", "Marcar rechazado…");
        await cut.Find("[role=dialog] textarea").InputAsync(new ChangeEventArgs { Value = "Sello ilegible" });
        var consultasAntes = mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Count();

        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Marcar rechazado").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Find("[role=dialog]").TextContent.Should().Contain("Esta acreditación ya no admite el cambio."));
        mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Count().Should().Be(consultasAntes);

        // Lo escrito se conserva: reintentar manda el mismo detalle sin volver a pedirlo («Describe el motivo del rechazo.»).
        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Marcar rechazado").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => mediador.Enviadas.OfType<MarcarAcreditacionRechazadaCommand>().Select(c => c.MotivoLiteral)
            .Should().Equal("Sello ilegible", "Sello ilegible"));
    }

    [Fact]
    public async Task Si_el_servidor_rechaza_marcar_subido_avisa_con_el_error_y_no_recarga()
    {
        var mediador = EnNalanda(("Luis Mora Gil", EstadoAcreditacion.PendienteDeSubir, null, Guid.NewGuid()));
        mediador.Interceptar = p => p is MarcarAcreditacionSubidaCommand
            ? Task.FromResult<object?>(Result.Fallo(Error.Crear("Acreditacion.NoEditable", "Esta acreditación ya no admite el cambio.")))
            : null;
        var cut = await AgruparPorPlataformaAsync(mediador);
        var consultasAntes = mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Count();

        await ElegirOpcionAsync(cut, "Luis Mora Gil", "Marcar subido");

        cut.WaitForAssertion(() => Toasts().Should().Contain(t => t.Tono == TonoToast.Error && t.Mensaje.Contains("ya no admite el cambio")));
        mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Count().Should().Be(consultasAntes);
    }

    [Fact]
    public async Task El_disparador_de_la_fila_es_el_boton_secundario_pequeno_de_la_aplicacion_y_el_panel_se_ancla_a_su_fila()
    {
        var cut = await AgruparPorPlataformaAsync(EnNalanda(("Ana Ruiz Pardo", EstadoAcreditacion.Rechazada, null, Guid.NewGuid())));
        var fila = FilaDe(cut, "Ana Ruiz Pardo");
        fila.QuerySelector(".menu-acciones")!.ClassList.Should().Contain("menu-acciones-compacto");
        fila.QuerySelector("button.menu-acciones-disparador")!.ClassList.Should().Contain("menu-acciones-disparador-compacto");

        var css = File.ReadAllText(RutaWeb("Components", "DesignSystem", "MenuAcciones.razor.css"));
        // 36 px de alto, 8×16 de relleno, radio y borde de control: los de `boton boton-secundario boton-pequeno`.
        css.Should().MatchRegex(@"\.menu-acciones-disparador-compacto\s*\{[^}]*min-height:\s*36px[^}]*padding:\s*var\(--space-2\) var\(--space-4\)[^}]*border-radius:\s*var\(--radius-md\)[^}]*border-color:\s*var\(--color-border-control\)");
        // El panel no depende de dejar sitio en la tabla: se ancla al disparador de su fila y se abre hacia el otro lado si no cabe.
        css.Should().Contain("anchor-scope: --menu-acciones").And.Contain("position-anchor: --menu-acciones").And.Contain("position-try-fallbacks: flip-block");
    }

    [Fact]
    public void Abrir_el_menu_no_reserva_alto_ni_desplaza_la_pagina()
    {
        var css = File.ReadAllText(RutaWeb("Features", "Documentos", "Pages", "Documentos.razor.css"));

        // El panel ya no se recorta por el overflow de la tabla (va anclado y fijo): ninguna regla de la tabla reserva alto mientras
        // hay un menú abierto (antes, :has(... [aria-expanded=true]) subía el padding-bottom a 17 rem y desplazaba la página).
        css.Should().NotContain(".menu-acreditacion", "no hay regla de la tabla para el menú de la fila")
            .And.NotMatchRegex(@":has\([^)]*aria-expanded", "ninguna regla de la tabla cambia según haya un menú abierto")
            .And.NotMatchRegex(@"padding-bottom:\s*\d+(\.\d+)?rem");
    }

    [Fact]
    public void El_pie_de_un_modal_con_boton_con_espera_no_estira_Cancelar_hasta_la_altura_de_la_envoltura()
    {
        var css = File.ReadAllText(RutaWeb("Components", "DesignSystem", "Modal.razor.css"));

        css.Should().MatchRegex(@"\.modal-pie:has\(\.boton-espera\)\s*\{[^}]*align-items:\s*flex-start");
    }

    private static string RutaWeb(params string[] partes)
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null && !Directory.Exists(Path.Combine(directorio.FullName, "src", "CaeManager.Web")))
            directorio = directorio.Parent;
        return Path.Combine([directorio!.FullName, "src", "CaeManager.Web", .. partes]);
    }

    private static Result RegistrarRechazo(List<MarcarAcreditacionRechazadaCommand> enviados, MarcarAcreditacionRechazadaCommand comando)
    {
        enviados.Add(comando);
        return Result.Exito();
    }

    private static MarcarAcreditacionAceptadaCommand Registrar(List<MarcarAcreditacionAceptadaCommand> enviados, MarcarAcreditacionAceptadaCommand comando)
    {
        enviados.Add(comando);
        return comando;
    }

    private static Result Registrar(List<MarcarAcreditacionSubidaCommand> enviados, MarcarAcreditacionSubidaCommand comando)
    {
        enviados.Add(comando);
        return Result.Exito();
    }
}
