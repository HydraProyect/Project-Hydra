using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Contactos.Commands.GuardarContactoAgenda;
using CaeManager.Application.Contactos.Queries.ObtenerAgendaContactos;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Contactos;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// El contacto de agenda es un <c>DrawerFormulario</c> por pasos («Panel Formulario TALVEG», como el alta de Trabajador): Datos, Qué recibe y
/// Documentación. Se fija todo lo que tenía el formulario en un solo modal (campos, rótulos, validaciones, estados y el comando que se envía) y lo que
/// aporta el kit: «Cancelar» cierra como la X (pregunta si hay datos escritos), el error del servidor sale en el aviso fijo y no en un toast, los
/// errores de campo los pintan los campos, el aviso de un intento anterior se va al corregir y «Siguiente» no avanza con «Datos» en error.
/// </summary>
public sealed class ModalContactoAgendaTests : BunitContext
{
    private readonly Mediador _mediador = new();
    private bool _visible = true;

    public ModalContactoAgendaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura();
        Services.AddSingleton<IMediator>(_mediador);
        Services.AddScoped<ToastService>();
        Services.AddLocalization();
    }

    private IRenderedComponent<ModalContactoAgenda> Renderizar() =>
        Render<ModalContactoAgenda>(p => p
            .Add(x => x.Visible, _visible)
            .Add(x => x.VisibleChanged, v => _visible = v)
            .Add(x => x.Tipo, TipoPropietarioAgenda.Subcontrata)
            .Add(x => x.PropietarioId, Guid.NewGuid()));

    private static IReadOnlyList<string> Alertas(IRenderedComponent<ModalContactoAgenda> cut) =>
        cut.FindAll("[role=dialog] [role=alert]").Select(a => a.TextContent.Trim()).ToList();

    private static async Task Escribir(IRenderedComponent<ModalContactoAgenda> cut, string etiqueta, string valor) =>
        await cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == etiqueta)
            .Find("input").InputAsync(new ChangeEventArgs { Value = valor });

    private static Task Pulsar(IRenderedComponent<ModalContactoAgenda> cut, string texto) =>
        cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == texto).ClickAsync(new MouseEventArgs());

    private static IReadOnlyList<string> BotonesDelPie(IRenderedComponent<ModalContactoAgenda> cut) =>
        cut.FindAll(".drawer-pie button").Select(b => b.TextContent.Trim()).ToList();

    private static IReadOnlyList<string> PasosDelIndicador(IRenderedComponent<ModalContactoAgenda> cut) =>
        cut.FindAll("ol.indicador-pasos .indicador-pasos-titulo").Select(t => t.TextContent.Trim()).ToList();

    private static string PasoActivo(IRenderedComponent<ModalContactoAgenda> cut) =>
        cut.Find("ol.indicador-pasos li.indicador-pasos-activo .indicador-pasos-titulo").TextContent.Trim();

    private static Task Siguiente(IRenderedComponent<ModalContactoAgenda> cut) => Pulsar(cut, "Siguiente");

    private static IElement Casilla(IRenderedComponent<ModalContactoAgenda> cut, string texto) =>
        cut.FindAll("label.campo-checkbox").Single(l => l.TextContent.Trim().StartsWith(texto, StringComparison.Ordinal)).QuerySelector("input")!;

    private static async Task Marcar(IRenderedComponent<ModalContactoAgenda> cut, string texto) =>
        await Casilla(cut, texto).ChangeAsync(new ChangeEventArgs { Value = true });

    /// <summary>Rellena «Datos» y deja el formulario en «Qué recibe».</summary>
    private static async Task RellenarDatosYAvanzar(IRenderedComponent<ModalContactoAgenda> cut)
    {
        await Escribir(cut, "Nombre", "Nuria Salas");
        await Escribir(cut, "Email", "nuria@example.test");
        await Siguiente(cut);
    }

    // ---- Pasos: estructura, pie y navegación -------------------------------------------------------------------------------

    [Fact]
    public void Se_abre_en_Datos_con_el_indicador_de_tres_pasos_y_los_cuatro_campos_de_siempre()
    {
        var cut = Renderizar();

        cut.Find("h2").TextContent.Trim().Should().Be("Nuevo contacto");
        PasosDelIndicador(cut).Should().Equal("Datos", "Qué recibe", "Documentación");
        PasoActivo(cut).Should().Be("Datos");
        cut.FindComponents<CampoTexto>().Select(c => c.Instance.Etiqueta)
            .Should().Equal("Nombre", "Email", "Teléfono (WhatsApp)", "Cargo");
        cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Email").Instance.Type.Should().Be("email");
        cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Teléfono (WhatsApp)").Instance.Type.Should().Be("tel");
        cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Teléfono (WhatsApp)").Instance.Placeholder.Should().Be("+34 600 00 00 00");
        cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Cargo").Instance.Placeholder.Should().Be("Finanzas, PRL, Vigilancia de la Salud…");
        cut.FindAll("input[type=checkbox]").Should().BeEmpty("las casillas son del paso «Qué recibe»");
    }

    [Fact]
    public async Task El_pie_ofrece_Cancelar_Guardar_y_terminar_y_Siguiente_hasta_el_ultimo_paso_donde_ofrece_Atras_y_Guardar()
    {
        var cut = Renderizar();
        BotonesDelPie(cut).Should().Equal("Cancelar", "Guardar y terminar", "Siguiente");

        await RellenarDatosYAvanzar(cut);
        BotonesDelPie(cut).Should().Equal("Atrás", "Guardar y terminar", "Siguiente");

        await Siguiente(cut);
        PasoActivo(cut).Should().Be("Documentación");
        BotonesDelPie(cut).Should().Equal("Atrás", "Guardar");
    }

    [Fact]
    public void Los_botones_del_pie_son_el_mismo_boton_del_sistema_y_solo_cambia_la_variante()
    {
        var cut = Renderizar();

        var tamanos = cut.FindAll(".drawer-pie button")
            .Select(b => b.ClassList.Where(c => c is "boton-pequeno" or "boton-medio" or "boton-grande").ToList())
            .ToList();

        tamanos.Should().HaveCount(3);
        tamanos.Should().OnlyContain(c => c.SequenceEqual(new[] { "boton-medio" }),
            "«Cancelar», «Guardar y terminar» y «Siguiente» comparten tamaño y radio (el del sistema, medio): solo difiere la variante");
        cut.FindAll(".drawer-pie button.boton-primario").Should().ContainSingle().Which.TextContent.Trim().Should().Be("Siguiente");
    }

    [Fact]
    public async Task Atras_vuelve_al_paso_anterior_sin_perder_lo_escrito()
    {
        var cut = Renderizar();
        await Escribir(cut, "Nombre", "Nuria Salas");
        await Escribir(cut, "Email", "nuria@example.test");
        await Escribir(cut, "Cargo", "PRL");
        await Siguiente(cut);

        await Pulsar(cut, "Atrás");

        PasoActivo(cut).Should().Be("Datos");
        cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Nombre").Instance.Valor.Should().Be("Nuria Salas");
        cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Cargo").Instance.Valor.Should().Be("PRL");
    }

    [Fact]
    public async Task Un_paso_visto_se_puede_pulsar_en_el_indicador_y_uno_por_ver_no()
    {
        var cut = Renderizar();
        cut.FindAll("ol.indicador-pasos button.indicador-pasos-boton").Should().BeEmpty("en un alta, ningún paso está visto todavía");

        await RellenarDatosYAvanzar(cut);
        await Siguiente(cut);
        cut.FindAll("ol.indicador-pasos button.indicador-pasos-boton").Select(b => b.TextContent.Trim())
            .Should().Equal("Datos", "Qué recibe");

        await cut.FindAll("ol.indicador-pasos button.indicador-pasos-boton").Single(b => b.TextContent.Trim() == "Datos")
            .ClickAsync(new MouseEventArgs());

        PasoActivo(cut).Should().Be("Datos");
        cut.FindAll("ol.indicador-pasos button.indicador-pasos-boton").Select(b => b.TextContent.Trim())
            .Should().Equal("Qué recibe");
    }

    [Fact]
    public async Task Siguiente_no_avanza_con_el_nombre_o_el_email_vacios_y_los_pintan_los_campos()
    {
        var cut = Renderizar();

        await Siguiente(cut);

        PasoActivo(cut).Should().Be("Datos");
        cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Nombre").Instance.MensajeError.Should().Be("El nombre es obligatorio.");
        cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Email").Instance.MensajeError
            .Should().Be("El email es obligatorio — la solicitud se envía siempre por correo.");

        await Escribir(cut, "Nombre", "Nuria");
        await Escribir(cut, "Email", "nuria@example.test");
        await Siguiente(cut);
        PasoActivo(cut).Should().Be("Qué recibe");
    }

    [Fact]
    public async Task Cancelar_sin_datos_cierra_y_con_datos_pregunta_como_la_X()
    {
        var cut = Renderizar();
        await Pulsar(cut, "Cancelar");
        _visible.Should().BeFalse("sin nada escrito «Cancelar» cierra sin preguntar");

        _visible = true;
        cut = Renderizar();
        await Escribir(cut, "Nombre", "Nuria Salas");
        await Pulsar(cut, "Cancelar");

        _visible.Should().BeTrue("con datos escritos no cierra de golpe");
        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().Contain("Descartar cambios");
    }

    [Fact]
    public async Task Atras_nunca_pregunta_aunque_haya_cambios_en_otro_paso()
    {
        var cut = Renderizar();
        await RellenarDatosYAvanzar(cut);

        await Pulsar(cut, "Atrás");

        _visible.Should().BeTrue("«Atrás» solo cambia de paso, no cierra ni pregunta");
        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().NotContain("Descartar cambios");
    }

    // ---- Datos: validación de Guardar ------------------------------------------------------------------------------------

    [Fact]
    public async Task Nombre_y_email_vacios_los_pintan_los_campos_y_no_se_envia_nada()
    {
        var cut = Renderizar();

        await Pulsar(cut, "Guardar y terminar");

        _mediador.Enviadas.OfType<GuardarContactoAgendaCommand>().Should().BeEmpty();
        cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Nombre").Instance.MensajeError.Should().Be("El nombre es obligatorio.");
        cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Email").Instance.MensajeError.Should().NotBeNullOrWhiteSpace();

        await Escribir(cut, "Nombre", "Nuria");
        cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Nombre").Instance.MensajeError
            .Should().BeNull("el error era del intento anterior");
    }

    [Fact]
    public async Task Guardar_y_terminar_desde_otro_paso_con_Datos_en_error_vuelve_a_Datos_y_no_envia_nada()
    {
        var cut = Renderizar();
        await RellenarDatosYAvanzar(cut);
        await Siguiente(cut);
        await cut.FindAll("ol.indicador-pasos button.indicador-pasos-boton").Single(b => b.TextContent.Trim() == "Datos")
            .ClickAsync(new MouseEventArgs());
        await Escribir(cut, "Email", "");
        // «Siguiente» no pasaría con el email vacío, pero el indicador deja ir a un paso ya visto: «Guardar y terminar» es quien lo ataja.
        await cut.FindAll("ol.indicador-pasos button.indicador-pasos-boton").Single(b => b.TextContent.Trim() == "Qué recibe")
            .ClickAsync(new MouseEventArgs());
        PasoActivo(cut).Should().Be("Qué recibe");

        await Pulsar(cut, "Guardar y terminar");

        _mediador.Enviadas.OfType<GuardarContactoAgendaCommand>().Should().BeEmpty();
        PasoActivo(cut).Should().Be("Datos", "los errores de campo están en «Datos» y allí se enseñan");
        cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Email").Instance.MensajeError.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Los_errores_de_Datos_solo_bloquean_el_clic_fuera_y_Escape_mientras_Datos_esta_a_la_vista()
    {
        var cut = Renderizar();
        await RellenarDatosYAvanzar(cut);
        await Siguiente(cut);
        await cut.FindAll("ol.indicador-pasos button.indicador-pasos-boton").Single(b => b.TextContent.Trim() == "Datos")
            .ClickAsync(new MouseEventArgs());
        await Escribir(cut, "Email", "");
        await cut.FindAll("ol.indicador-pasos button.indicador-pasos-boton").Single(b => b.TextContent.Trim() == "Qué recibe")
            .ClickAsync(new MouseEventArgs());
        await Pulsar(cut, "Guardar y terminar");
        PasoActivo(cut).Should().Be("Datos");
        cut.FindComponent<Drawer>().Instance.CerrarAlHacerClicFuera.Should().BeFalse("con el error de campo a la vista no se cierra por fuera");

        await cut.FindAll("ol.indicador-pasos button.indicador-pasos-boton").Single(b => b.TextContent.Trim() == "Qué recibe")
            .ClickAsync(new MouseEventArgs());

        PasoActivo(cut).Should().Be("Qué recibe");
        cut.FindComponent<Drawer>().Instance.CerrarAlHacerClicFuera.Should().BeTrue(
            "el error sigue en «Datos» pero desde otro paso no se ve, y no debe bloquear Escape ni el clic fuera");
    }

    [Fact]
    public async Task Marcar_una_casilla_o_escribir_la_nota_en_Que_recibe_cuenta_como_cambio_y_la_X_pregunta()
    {
        var contacto = new ContactoAgendaDto(
            Guid.NewGuid(), "Marta Ruiz", "marta@example.test", null, null, null, false, false, false, [], [], [], NotaVisible: true);
        var cut = RenderizarEdicion(contacto);
        await Siguiente(cut);
        PasoActivo(cut).Should().Be("Qué recibe");
        await cut.Find(".drawer-cerrar").ClickAsync(new MouseEventArgs());
        _visible.Should().BeFalse("sin tocar nada la X cierra sin preguntar");

        _visible = true;
        cut = RenderizarEdicion(contacto);
        await Siguiente(cut);
        await Marcar(cut, "Recibe la facturación");
        await cut.Find(".drawer-cerrar").ClickAsync(new MouseEventArgs());
        _visible.Should().BeTrue("una casilla marcada en otro paso es trabajo sin guardar");
        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().Contain("Descartar cambios");

        _visible = true;
        cut = RenderizarEdicion(contacto);
        await Siguiente(cut);
        await cut.FindComponent<CampoTextarea>().Find("textarea").InputAsync(new ChangeEventArgs { Value = "Avisar antes de llamar" });
        await cut.Find(".drawer-cerrar").ClickAsync(new MouseEventArgs());
        _visible.Should().BeTrue("la nota escrita en otro paso es trabajo sin guardar");
        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().Contain("Descartar cambios");
    }

    // ---- Qué recibe y Documentación ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Que_recibe_ofrece_las_tres_casillas_de_envio_los_tres_roles_y_la_nota_con_sus_rotulos()
    {
        var cut = Renderizar();
        await RellenarDatosYAvanzar(cut);

        cut.FindAll("label.campo-checkbox").Select(l => l.TextContent.Trim()).Should().Equal(
            "Contacto predeterminado — recibe lo que no tenga destinatario propio",
            "Recibe la programación de visitas",
            "Recibe la facturación",
            "Responsable de PRL",
            "Representante legal",
            "Contacto CAE");
        cut.FindAll("div.campo[role=group] > label").Select(l => l.TextContent.Trim()).Should().Equal("Recibe", "Rol del contacto");
        var nota = cut.FindComponent<CampoTextarea>();
        nota.Instance.Etiqueta.Should().Be("Nota para Comunicaciones");
        cut.Find("p.agenda-nota-ayuda").TextContent.Trim().Should().Contain("Solo la ve tu equipo, nunca el contacto.");
        cut.FindAll("input[type=checkbox][id]").Should().OnlyContain(i => cut.FindAll("label[for='" + i.Id + "']").Count == 1,
            "cada casilla se asocia a su etiqueta por for/id (D-08)");
    }

    [Fact]
    public async Task Documentacion_ofrece_el_selector_con_buscador_y_solo_relacionados_y_marca_lo_que_se_elige()
    {
        _mediador.Tipos = [Tipo("Formación Art. 19"), Tipo("Seguro de responsabilidad civil"), Tipo("Reconocimiento médico")];
        var cut = Renderizar();
        await RellenarDatosYAvanzar(cut);
        await Siguiente(cut);

        cut.Find("div.agenda-contacto-documentacion .campo > label").TextContent.Trim().Should().Be("Documentación que se le pide");
        cut.Find("div.agenda-contacto-documentacion input[placeholder='Buscar…']").Should().NotBeNull();
        cut.Markup.Should().Contain("Solo mostrar relacionados");
        cut.FindAll("div.agenda-contacto-documentacion .lista-seleccion-multiple label.campo-checkbox").Select(l => l.TextContent.Trim())
            .Should().BeEquivalentTo("Formación Art. 19", "Seguro de responsabilidad civil", "Reconocimiento médico");

        await cut.FindAll("div.agenda-contacto-documentacion .lista-seleccion-multiple label.campo-checkbox")
            .Single(l => l.TextContent.Trim() == "Reconocimiento médico").QuerySelector("input")!
            .ChangeAsync(new ChangeEventArgs { Value = true });
        await Pulsar(cut, "Guardar");

        _mediador.Enviadas.OfType<GuardarContactoAgendaCommand>().Should().ContainSingle().Which.TipoDocumentoIds
            .Should().Equal(_mediador.Tipos[2].Id);
    }

    [Fact]
    public async Task Documentacion_con_el_catalogo_entero_lo_lista_sin_paginar_para_no_competir_con_el_Siguiente_del_pie()
    {
        _mediador.Tipos = Enumerable.Range(1, 87).Select(i => Tipo($"Tipo {i}")).ToList();
        var cut = Renderizar();
        await RellenarDatosYAvanzar(cut);
        await Siguiente(cut);

        cut.FindAll("div.agenda-contacto-documentacion .lista-seleccion-multiple label.campo-checkbox").Should().HaveCount(87);
        cut.FindComponents<PaginadorSimple>().Should().BeEmpty("sin «Página 1 de 5» ni «Siguiente →» dentro del paso");
        BotonesDelPie(cut).Should().Equal("Atrás", "Guardar");
    }

    [Fact]
    public async Task Documentacion_sin_catalogo_lo_dice_y_no_bloquea_el_alta()
    {
        _mediador.Tipos = [];
        var cut = Renderizar();
        await RellenarDatosYAvanzar(cut);
        await Siguiente(cut);

        cut.Markup.Should().Contain("No hay tipos de documento configurados.");
        await Pulsar(cut, "Guardar");

        _mediador.Enviadas.OfType<GuardarContactoAgendaCommand>().Should().ContainSingle().Which.TipoDocumentoIds.Should().BeEmpty();
    }

    // ---- Guardado: el comando es el de siempre -------------------------------------------------------------------------------

    [Fact]
    public async Task Un_alta_que_recorre_los_tres_pasos_envia_un_solo_comando_con_todo_lo_escrito()
    {
        _mediador.Tipos = [Tipo("Formación Art. 19"), Tipo("Seguro de responsabilidad civil")];
        var propietario = Guid.NewGuid();
        var guardado = Guid.Empty;
        var cut = Render<ModalContactoAgenda>(p => p
            .Add(x => x.Visible, _visible)
            .Add(x => x.VisibleChanged, v => _visible = v)
            .Add(x => x.Tipo, TipoPropietarioAgenda.Cliente)
            .Add(x => x.PropietarioId, propietario)
            .Add(x => x.OnGuardado, id => guardado = id));

        await Escribir(cut, "Nombre", "Nuria Salas");
        await Escribir(cut, "Email", "nuria@example.test");
        await Escribir(cut, "Teléfono (WhatsApp)", "+34 600 00 00 00");
        await Escribir(cut, "Cargo", "PRL");
        await Siguiente(cut);
        await Marcar(cut, "Contacto predeterminado");
        await Marcar(cut, "Recibe la programación de visitas");
        await Marcar(cut, "Recibe la facturación");
        await Marcar(cut, "Responsable de PRL");
        await Marcar(cut, "Contacto CAE");
        await cut.FindComponent<CampoTextarea>().Find("textarea").InputAsync(new ChangeEventArgs { Value = "Prefiere WhatsApp" });
        await Siguiente(cut);
        await cut.FindAll("div.agenda-contacto-documentacion label.campo-checkbox")
            .Single(l => l.TextContent.Trim() == "Formación Art. 19").QuerySelector("input")!
            .ChangeAsync(new ChangeEventArgs { Value = true });
        await Pulsar(cut, "Guardar");

        var comando = _mediador.Enviadas.OfType<GuardarContactoAgendaCommand>().Should().ContainSingle().Which;
        comando.Tipo.Should().Be(TipoPropietarioAgenda.Cliente);
        comando.PropietarioId.Should().Be(propietario);
        comando.ContactoId.Should().BeNull("es un alta");
        comando.Nombre.Should().Be("Nuria Salas");
        comando.Email.Should().Be("nuria@example.test");
        comando.Telefono.Should().Be("+34 600 00 00 00");
        comando.Cargo.Should().Be("PRL");
        comando.Notas.Should().Be("Prefiere WhatsApp");
        comando.NotasAlCargar.Should().BeNull();
        comando.EsPredeterminado.Should().BeTrue();
        comando.RecibeProgramacionVisitas.Should().BeTrue();
        comando.RecibeFacturacion.Should().BeTrue();
        comando.Roles.Should().BeEquivalentTo([RolContacto.ResponsablePrl, RolContacto.ContactoCae]);
        comando.TipoDocumentoIds.Should().Equal(_mediador.Tipos[0].Id);
        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle().Which.Mensaje.Should().Be("Contacto añadido.");
        guardado.Should().NotBeEmpty("el alta avisa con el Id guardado");
        _visible.Should().BeFalse("guardado correctamente cierra");
    }

    [Fact]
    public async Task Guardar_y_terminar_guarda_lo_escrito_hasta_aqui_sin_pasar_por_los_pasos_que_faltan()
    {
        var cut = Renderizar();
        await RellenarDatosYAvanzar(cut);

        await Pulsar(cut, "Guardar y terminar");

        var comando = _mediador.Enviadas.OfType<GuardarContactoAgendaCommand>().Should().ContainSingle().Which;
        comando.Nombre.Should().Be("Nuria Salas");
        comando.Roles.Should().BeEmpty();
        comando.TipoDocumentoIds.Should().BeEmpty();
        _visible.Should().BeFalse();
    }

    [Fact]
    public async Task La_agenda_vacia_abre_el_alta_con_predeterminado_marcado()
    {
        var cut = Render<ModalContactoAgenda>(p => p
            .Add(x => x.Visible, _visible)
            .Add(x => x.VisibleChanged, v => _visible = v)
            .Add(x => x.Tipo, TipoPropietarioAgenda.Subcontrata)
            .Add(x => x.PropietarioId, Guid.NewGuid())
            .Add(x => x.PredeterminadoPorDefecto, true));
        await RellenarDatosYAvanzar(cut);

        Casilla(cut, "Contacto predeterminado").HasAttribute("checked").Should().BeTrue();
    }

    [Fact]
    public async Task El_rechazo_del_servidor_sale_en_el_aviso_fijo_no_en_un_toast_y_escribir_lo_quita()
    {
        _mediador.FallaGuardarCon = "Ya existe un contacto con ese email.";
        var cut = Renderizar();
        await Escribir(cut, "Nombre", "Nuria Salas");
        await Escribir(cut, "Email", "nuria@example.test");
        await Siguiente(cut);
        Alertas(cut).Should().BeEmpty("control positivo: antes del intento no hay aviso");

        await Pulsar(cut, "Guardar y terminar");

        _mediador.Enviadas.OfType<GuardarContactoAgendaCommand>().Should().ContainSingle("control positivo: se intentó");
        Alertas(cut).Should().Equal("Ya existe un contacto con ese email.");
        cut.FindAll("[role=dialog] .drawer-cuerpo [role=alert]").Should().BeEmpty("el aviso va fuera del cuerpo desplazable (D-20)");
        Services.GetRequiredService<ToastService>().Mensajes.Should().BeEmpty("el motivo ya no es un toast que desaparece");
        _visible.Should().BeTrue("un rechazo no cierra el panel");

        await Pulsar(cut, "Atrás");
        await Escribir(cut, "Teléfono (WhatsApp)", "+34 600 00 00 00");
        Alertas(cut).Should().BeEmpty("corregir cualquier campo, no solo el nombre, retira el motivo del intento anterior y devuelve el clic fuera y Escape");

        await Siguiente(cut);
        await Pulsar(cut, "Guardar y terminar");
        Alertas(cut).Should().Equal("Ya existe un contacto con ese email.");

        await cut.FindAll("input[type=checkbox]")[1].ChangeAsync(new ChangeEventArgs { Value = true });
        Alertas(cut).Should().BeEmpty("una casilla también cuenta");

        await Pulsar(cut, "Guardar y terminar");
        await Pulsar(cut, "Atrás");
        await Escribir(cut, "Nombre", "Nuria Salas Ortiz");
        await Siguiente(cut);
        _mediador.FallaGuardarCon = null;
        await Pulsar(cut, "Guardar y terminar");

        _mediador.Enviadas.OfType<GuardarContactoAgendaCommand>().Should().HaveCount(4);
        _visible.Should().BeFalse("guardado correctamente cierra");
    }

    // ---- Edición ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Editar_abre_en_Datos_con_los_datos_cargados_deja_saltar_a_cualquier_paso_y_guarda_con_su_Id()
    {
        var tipo = Tipo("Formación Art. 19");
        _mediador.Tipos = [tipo, Tipo("Seguro de responsabilidad civil")];
        var id = Guid.NewGuid();
        var contacto = new ContactoAgendaDto(id, "Marta Ruiz", "marta@example.test", "+34 611 11 11 11", "Finanzas", "Nota previa",
            true, false, true, [tipo.Id], ["Formación Art. 19"], [RolContacto.RepresentanteLegal], NotaVisible: true);
        var cut = RenderizarEdicion(contacto);

        cut.Find("h2").TextContent.Trim().Should().Be("Editar contacto");
        PasoActivo(cut).Should().Be("Datos");
        cut.FindComponents<CampoTexto>().Select(c => c.Instance.Valor)
            .Should().Equal("Marta Ruiz", "marta@example.test", "+34 611 11 11 11", "Finanzas");
        cut.FindAll("ol.indicador-pasos button.indicador-pasos-boton").Select(b => b.TextContent.Trim())
            .Should().Equal("Qué recibe", "Documentación");

        await cut.FindAll("ol.indicador-pasos button.indicador-pasos-boton").Single(b => b.TextContent.Trim() == "Qué recibe")
            .ClickAsync(new MouseEventArgs());
        Casilla(cut, "Contacto predeterminado").HasAttribute("checked").Should().BeTrue();
        Casilla(cut, "Recibe la programación de visitas").HasAttribute("checked").Should().BeFalse();
        Casilla(cut, "Recibe la facturación").HasAttribute("checked").Should().BeTrue();
        Casilla(cut, "Representante legal").HasAttribute("checked").Should().BeTrue();
        Casilla(cut, "Responsable de PRL").HasAttribute("checked").Should().BeFalse();
        cut.FindComponent<CampoTextarea>().Instance.Valor.Should().Be("Nota previa");

        await cut.FindAll("ol.indicador-pasos button.indicador-pasos-boton").Single(b => b.TextContent.Trim() == "Documentación")
            .ClickAsync(new MouseEventArgs());
        cut.FindAll("div.agenda-contacto-documentacion .lista-seleccion-multiple label.campo-checkbox input").Select(i => i.HasAttribute("checked"))
            .Should().Equal(true, false);
        await Pulsar(cut, "Guardar");

        var comando = _mediador.Enviadas.OfType<GuardarContactoAgendaCommand>().Should().ContainSingle().Which;
        comando.ContactoId.Should().Be(id);
        comando.Nombre.Should().Be("Marta Ruiz");
        comando.Telefono.Should().Be("+34 611 11 11 11");
        comando.Cargo.Should().Be("Finanzas");
        comando.Notas.Should().Be("Nota previa");
        comando.EsPredeterminado.Should().BeTrue();
        comando.RecibeFacturacion.Should().BeTrue();
        comando.Roles.Should().Equal(RolContacto.RepresentanteLegal);
        comando.TipoDocumentoIds.Should().Equal(tipo.Id);
        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle().Which.Mensaje.Should().Be("Contacto actualizado.");
    }

    [Fact]
    public async Task Reabrir_el_panel_vuelve_a_Datos_y_a_un_alta_limpia()
    {
        var cut = Renderizar();
        await RellenarDatosYAvanzar(cut);
        PasoActivo(cut).Should().Be("Qué recibe");

        await cut.InvokeAsync(() => cut.Render(p => p.Add(x => x.Visible, false)));
        await cut.InvokeAsync(() => cut.Render(p => p.Add(x => x.Visible, true)));

        PasoActivo(cut).Should().Be("Datos");
        cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Nombre").Instance.Valor.Should().BeEmpty();
        cut.FindAll("ol.indicador-pasos button.indicador-pasos-boton").Should().BeEmpty();
    }

    [Fact]
    public async Task Guardar_envia_la_nota_que_el_formulario_cargo_al_abrir_aunque_el_contacto_se_refresque()
    {
        static ContactoAgendaDto Contacto(Guid id, string notas) =>
            new(id, "Marta Ruiz", "marta@example.test", null, null, notas, false, false, false, [], [], [], NotaVisible: true);

        var id = Guid.NewGuid();
        var cut = Render<ModalContactoAgenda>(p => p
            .Add(x => x.Visible, _visible)
            .Add(x => x.VisibleChanged, v => _visible = v)
            .Add(x => x.Tipo, TipoPropietarioAgenda.Subcontrata)
            .Add(x => x.PropietarioId, Guid.NewGuid())
            .Add(x => x.Contacto, Contacto(id, "Nota vista al abrir")));

        await cut.InvokeAsync(() => cut.Render(p => p.Add(x => x.Contacto, Contacto(id, "Nota fijada desde el panel"))));
        await Pulsar(cut, "Guardar y terminar");

        var comando = _mediador.Enviadas.OfType<GuardarContactoAgendaCommand>().Should().ContainSingle().Which;
        comando.Notas.Should().Be("Nota vista al abrir");
        comando.NotasAlCargar.Should().Be("Nota vista al abrir");
    }

    [Fact]
    public async Task El_alta_y_la_edicion_con_la_nota_permitida_ofrecen_el_campo_en_Que_recibe()
    {
        var alta = Renderizar();
        alta.FindComponents<CampoTextarea>().Should().BeEmpty("la nota es del paso «Qué recibe»");
        await RellenarDatosYAvanzar(alta);
        alta.FindComponents<CampoTextarea>().Should().ContainSingle("control positivo: un alta no tiene nota que proteger");

        var contacto = new ContactoAgendaDto(
            Guid.NewGuid(), "Marta Ruiz", "marta@example.test", null, null, null, false, false, false, [], [], [], NotaVisible: true);
        var edicion = RenderizarEdicion(contacto);
        await Siguiente(edicion);
        edicion.FindComponents<CampoTextarea>().Should().ContainSingle();
    }

    // Notas con texto y NotaVisible en false no lo produce la consulta: fija que la pantalla obedece a NotaVisible aunque le llegue el texto.
    [Theory]
    [InlineData(null)]
    [InlineData("Nota que este lector no puede ver")]
    public async Task Sin_la_nota_permitida_no_se_ofrece_el_campo_y_el_guardado_no_la_toca(string? notas)
    {
        var contacto = new ContactoAgendaDto(
            Guid.NewGuid(), "Marta Ruiz", "marta@example.test", null, null, notas, false, false, false, [], [], []);
        var cut = RenderizarEdicion(contacto);
        await Siguiente(cut);

        PasoActivo(cut).Should().Be("Qué recibe");
        cut.FindComponents<CampoTextarea>().Should().BeEmpty();
        cut.Markup.Should().NotContain("Nota que este lector no puede ver");

        await Pulsar(cut, "Guardar y terminar");

        var comando = _mediador.Enviadas.OfType<GuardarContactoAgendaCommand>().Should().ContainSingle().Which;
        comando.Notas.Should().BeNull();
        comando.NotasAlCargar.Should().BeNull("Notas y NotasAlCargar iguales significan «no tocada»");
    }

    private IRenderedComponent<ModalContactoAgenda> RenderizarEdicion(ContactoAgendaDto contacto) =>
        Render<ModalContactoAgenda>(p => p
            .Add(x => x.Visible, _visible)
            .Add(x => x.VisibleChanged, v => _visible = v)
            .Add(x => x.Tipo, TipoPropietarioAgenda.Subcontrata)
            .Add(x => x.PropietarioId, Guid.NewGuid())
            .Add(x => x.Contacto, contacto));

    private static TipoDocumentoListaDto Tipo(string nombre) =>
        new(Guid.NewGuid(), nombre, null, false, 1, AmbitoAplicacion.Trabajador, default, default, null, null, null, null, false, false, false, default, []);

    private sealed class Mediador : IMediator
    {
        public List<object> Enviadas { get; } = [];
        public string? FallaGuardarCon { get; set; }
        public IReadOnlyList<TipoDocumentoListaDto> Tipos { get; set; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            object valor = request switch
            {
                GuardarContactoAgendaCommand when FallaGuardarCon is { } motivo => Result.Fallo<Guid>(Error.Crear("contacto.rechazado", motivo)),
                GuardarContactoAgendaCommand => Result.Exito(Guid.NewGuid()),
                ObtenerTiposDocumentoQuery => Tipos,
                _ => throw new NotSupportedException(request.GetType().Name)
            };
            return Task.FromResult((TResponse)valor);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }
}
