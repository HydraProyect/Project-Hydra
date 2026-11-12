using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores.Commands.EditarFichaTrabajador;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadorPorId;
using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Trabajadores.Components;
using CaeManager.Web.Features.Trabajadores.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

public partial class Trabajador360Gen2Tests
{
    private static async Task CambiarCampoFicha(IRenderedComponent<EditarFichaTrabajadorDrawer> drawer, string etiqueta, string valor)
    {
        var id = drawer.FindAll("label").Single(l => l.TextContent.Trim() == etiqueta).GetAttribute("for");
        await drawer.Find($"#{id}").InputAsync(new ChangeEventArgs { Value = valor });
        await drawer.Find($"#{id}").BlurAsync(new FocusEventArgs());
    }

    // La edición es por pasos: en el último, «Guardar»; en uno intermedio, «Guardar y terminar», que envía la misma orden única.
    private static async Task GuardarFicha(IRenderedComponent<EditarFichaTrabajadorDrawer> drawer)
    {
        var botones = drawer.FindAll("button");
        var guardar = botones.FirstOrDefault(b => b.TextContent.Trim() == "Guardar")
            ?? botones.Single(b => b.TextContent.Trim() == "Guardar y terminar");
        await guardar.ClickAsync(new MouseEventArgs());
    }

    private static string PasoActivoFicha(IRenderedComponent<EditarFichaTrabajadorDrawer> drawer) =>
        drawer.Find("li.indicador-pasos-activo .indicador-pasos-titulo").TextContent.Trim();

    private static Task PulsarFicha(IRenderedComponent<EditarFichaTrabajadorDrawer> drawer, string boton) =>
        drawer.FindAll("button").Single(b => b.TextContent.Trim() == boton).ClickAsync(new MouseEventArgs());

    // Avanza con «Siguiente» hasta el paso pedido; si un paso lo bloquea (errores), se queda ahí y la aserción posterior falla.
    private static async Task IrAPasoFicha(IRenderedComponent<EditarFichaTrabajadorDrawer> drawer, string paso)
    {
        for (var i = 0; i < 4 && PasoActivoFicha(drawer) != paso; i++) await PulsarFicha(drawer, "Siguiente");
    }

    private static string[] EtiquetasDelPasoFicha(IRenderedComponent<EditarFichaTrabajadorDrawer> drawer) =>
        drawer.FindComponents<CampoTexto>().Select(c => c.Instance.Etiqueta ?? "").ToArray();

    private static async Task<IRenderedComponent<EditarFichaTrabajadorDrawer>> AbrirEditorFicha(IRenderedComponent<TrabajadorDetalle> cut)
    {
        await AbrirPestanaAsync(cut, "Ficha");
        await cut.Find("[data-caja='altas-plataforma'] button").ClickAsync(new MouseEventArgs());
        return cut.FindComponent<EditarFichaTrabajadorDrawer>();
    }

    [Fact]
    public async Task El_drawer_envia_nombre_documento_y_marca_en_una_sola_orden()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        m.Detalles[id] = m.Detalles[id] with { ExtranjeroCobrableVisible = true };
        var drawer = await AbrirEditorFicha(Renderizar(id));
        await CambiarCampoFicha(drawer, "Nombre", "Marta");
        await CambiarCampoFicha(drawer, "Apellidos", "Salas");
        await CambiarCampoFicha(drawer, "Documento de identidad", "AB123456");
        await IrAPasoFicha(drawer, "Situación");
        drawer.Find("[data-campo='extranjero-cobrable'] input").HasAttribute("checked").Should().BeTrue();
        await drawer.Find("[data-campo='extranjero-cobrable'] input").ChangeAsync(new ChangeEventArgs { Value = false });
        await GuardarFicha(drawer);
        var c = m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().ContainSingle().Subject;
        c.Nombre.Should().Be("Marta"); c.Apellidos.Should().Be("Salas"); c.Documento.Should().Be("AB123456");
        c.ExtranjeroCobrable.Should().Be(new MarcaExtranjeroCobrableTrabajador(false));
        m.Enviadas.OfType<CaeManager.Application.Trabajadores.Commands.CorregirDocumentoIdentidadTrabajador.CorregirDocumentoIdentidadTrabajadorCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task Volver_al_documento_cargado_no_envia_una_marca_a_mano_que_nadie_toco()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        m.Detalles[id] = m.Detalles[id] with
        {
            ExtranjeroCobrableVisible = true,
            Dni = "12345678Z",
            ExtranjeroCobrablePorDocumento = false,
            MarcaExtranjeroCobrable = true
        };
        var drawer = await AbrirEditorFicha(Renderizar(id));
        await CambiarCampoFicha(drawer, "Documento de identidad", "AB123456");
        await CambiarCampoFicha(drawer, "Documento de identidad", "12345678Z");
        await IrAPasoFicha(drawer, "Situación");
        drawer.Find("[data-campo='extranjero-cobrable'] input").HasAttribute("checked").Should().BeTrue();
        await GuardarFicha(drawer);
        var c = m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().ContainSingle().Subject;
        c.Documento.Should().BeNull();
        c.ExtranjeroCobrable.Should().BeNull();
    }

    [Fact]
    public async Task Las_tres_cajas_tienen_los_rotulos_y_valores_exactos_de_la_maqueta()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        m.Detalles[id] = m.Detalles[id] with
        {
            NumeroAfiliacionSeguridadSocial = "41123456789",
            Puesto = "Oficial 1.ª electricista",
            FechaNacimiento = new(1988, 3, 14),
            Nacionalidad = "Colombiana",
            TipoVinculo = "Plantilla",
            FechaAltaEmpresa = new(2023, 5, 2),
            FiguraEnRnt = "Sí, desde mayo de 2023",
            TipoPermisoTrabajo = "Residencia y trabajo por cuenta ajena",
            CaducidadPermisoTrabajo = new(2027, 2, 18),
            MotivoPermisoTrabajo = "Se identifica con NIE",
            PermisoTrabajoVisible = true
        };
        var cut = Renderizar(id); await AbrirPestanaAsync(cut, "Ficha");
        cut.FindComponent<CajasFicha>().Instance.Cajas.Select(c => c.Clave).Should().Equal("altas-plataforma", "vinculo-empresa", "permiso-trabajo", "agenda-empleador");
        cut.FindAll("[data-caja='altas-plataforma'] dt").Select(d => d.TextContent.Trim()).Should().Equal(
            "N.º de afiliación a la Seguridad Social", "Puesto u oficio", "Fecha de nacimiento", "Nacionalidad");
        cut.FindAll("[data-caja='vinculo-empresa'] dt").Select(d => d.TextContent.Trim()).Should().Equal(
            "Tipo de vínculo", "Alta en la empresa", "Baja", "Figura en el RNT");
        cut.FindAll("[data-caja='permiso-trabajo'] dt").Select(d => d.TextContent.Trim()).Should().Equal("Tipo", "Caduca", "Motivo");
        cut.Find(".cajas-ficha").TextContent.Should().Contain("41 1234567 89").And.Contain("Colombiana")
            .And.Contain("02/05/2023").And.Contain("Sí, desde mayo de 2023").And.Contain("18/02/2027").And.Contain("Se identifica con NIE");
        cut.FindAll("[data-caja='vinculo-empresa'] dd")[2].TextContent.Trim().Should().Be("Sigue de alta", "la maqueta lo rotula así cuando hay alta y no baja");
    }

    // Decisión del 2026-10-15: al Usuario de Cliente el servidor no le manda el permiso de trabajo
    // (PermisoTrabajoVisible = false); la pantalla retira la caja entera en vez de pintarla vacía.
    [Fact]
    public async Task Sin_permiso_de_trabajo_visible_la_ficha_no_pinta_su_caja_y_conserva_afiliacion_y_vinculo()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        m.Detalles[id] = m.Detalles[id] with
        {
            NumeroAfiliacionSeguridadSocial = "41123456789",
            TipoVinculo = "Plantilla",
            PermisoTrabajoVisible = false
        };
        var cut = Renderizar(id); await AbrirPestanaAsync(cut, "Ficha");
        cut.FindComponent<CajasFicha>().Instance.Cajas.Select(c => c.Clave).Should().Equal("altas-plataforma", "vinculo-empresa", "agenda-empleador");
        cut.FindAll("[data-caja='permiso-trabajo']").Should().BeEmpty();
        cut.Find(".cajas-ficha").TextContent.Should().Contain("41 1234567 89").And.Contain("Plantilla");
    }

    [Fact]
    public async Task Precarga_envia_los_once_campos_con_su_version_y_refresca_tras_guardar()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id); var version = m.Detalles[id].Version;
        m.Detalles[id] = m.Detalles[id] with { Nacionalidad = "Colombiana", FiguraEnRnt = "Sí, desde mayo de 2023" };
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);
        // Los 13 campos de texto de siempre, repartidos por pasos: ninguno se pierde ni se repite.
        EtiquetasDelPasoFicha(drawer).Should().Equal("Documento de identidad", "Nombre", "Apellidos", "Fecha de nacimiento", "Nacionalidad");
        drawer.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Nacionalidad").Instance.Valor.Should().Be("Colombiana");
        drawer.FindComponent<DrawerFormulario>().Instance.HayCambios().Should().BeFalse();
        await CambiarCampoFicha(drawer, "Fecha de nacimiento", "1988-03-14");
        await PulsarFicha(drawer, "Siguiente");
        PasoActivoFicha(drawer).Should().Be("Trabajo");
        EtiquetasDelPasoFicha(drawer).Should().Equal("Tipo de vínculo", "Alta en la empresa", "Baja");
        await CambiarCampoFicha(drawer, "Tipo de vínculo", "Plantilla");
        await CambiarCampoFicha(drawer, "Alta en la empresa", "2023-05-02");
        await CambiarCampoFicha(drawer, "Baja", "2026-10-15");
        await PulsarFicha(drawer, "Siguiente");
        PasoActivoFicha(drawer).Should().Be("Situación");
        EtiquetasDelPasoFicha(drawer).Should().Equal(
            "N.º de afiliación a la Seguridad Social", "Figura en el RNT", "Tipo", "Caduca", "Motivo");
        await CambiarCampoFicha(drawer, "N.º de afiliación a la Seguridad Social", "41 1234567 89");
        await CambiarCampoFicha(drawer, "Tipo", "Residencia y trabajo por cuenta ajena");
        await CambiarCampoFicha(drawer, "Caduca", "2027-02-18");
        await CambiarCampoFicha(drawer, "Motivo", "Se identifica con NIE");
        drawer.FindComponent<DrawerFormulario>().Instance.HayCambios().Should().BeTrue();
        await GuardarFicha(drawer);
        var c = m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().ContainSingle().Subject;
        c.Id.Should().Be(id); c.Version.Should().Be(version); c.NumeroAfiliacionSeguridadSocial.Should().Be("41 1234567 89");
        c.Puesto.Should().Be("Oficial de 1.ª — montaje"); c.DelegacionEmpresaYOficio.Should().BeNull("sin cambiar la Delegación ni el Oficio no se envían"); c.FechaNacimiento.Should().Be(new DateOnly(1988, 3, 14));
        c.Nacionalidad.Should().Be("Colombiana"); c.TipoVinculo.Should().Be("Plantilla");
        c.FechaAltaEmpresa.Should().Be(new DateOnly(2023, 5, 2)); c.FechaBajaEmpresa.Should().Be(new DateOnly(2026, 10, 15));
        c.FiguraEnRnt.Should().Be("Sí, desde mayo de 2023"); c.TipoPermisoTrabajo.Should().Be("Residencia y trabajo por cuenta ajena");
        c.CaducidadPermisoTrabajo.Should().Be(new DateOnly(2027, 2, 18)); c.MotivoPermisoTrabajo.Should().Be("Se identifica con NIE");
        cut.FindAll("[role=dialog]").Should().BeEmpty();
        cut.Find("[data-caja='altas-plataforma']").TextContent.Should().Contain("Oficial de 1.ª — montaje");
        m.Enviadas.OfType<ObtenerTrabajadorPorIdQuery>().Should().HaveCount(3, "carga de página, apertura y refresco");
    }

    // Decisión del 2026-10-15: el tipo de contratación se elige en la ficha (cuenta ajena, autónomo o voluntario).
    [Fact]
    public async Task El_selector_de_tipo_de_contratacion_precarga_el_valor_y_envia_el_elegido()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        m.Detalles[id] = m.Detalles[id] with { TipoContratacion = TipoContratacion.Autonomo };
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);
        await IrAPasoFicha(drawer, "Trabajo");
        var selector = drawer.FindComponents<CampoSelect>().Single(c => c.Instance.Etiqueta == "Tipo de contratación");
        selector.Instance.Valor.Should().Be("Autonomo", "precarga el tipo guardado, no el valor por defecto");
        selector.FindAll("option").Select(o => $"{o.GetAttribute("value")}={o.TextContent.Trim()}")
            .Should().Equal("CuentaAjena=Cuenta ajena", "Autonomo=Autónomo", "Voluntario=Voluntario");
        drawer.FindComponent<DrawerFormulario>().Instance.HayCambios().Should().BeFalse();

        await selector.Find("select").ChangeAsync(new ChangeEventArgs { Value = "Voluntario" });
        drawer.FindComponent<DrawerFormulario>().Instance.HayCambios().Should().BeTrue("cambiar el tipo es un cambio sin guardar");
        await GuardarFicha(drawer);

        m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().ContainSingle()
            .Which.TipoContratacion.Should().Be(TipoContratacion.Voluntario);
    }

    [Fact]
    public async Task El_conflicto_conserva_el_borrador_y_reintenta_solo_con_la_version_original()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id); var version = m.Detalles[id].Version;
        m.ResultadoFicha = Result.Fallo(Error.Crear(ConcurrenciaOptimista.CodigoConflicto, "Otra persona ha guardado cambios."));
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);
        await CambiarCampoFicha(drawer, "Nacionalidad", "Colombiana");
        await GuardarFicha(drawer);
        drawer.Markup.Should().Contain("Otra persona ha guardado cambios.");
        m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().ContainSingle();
        await GuardarFicha(drawer);
        m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().HaveCount(2)
            .And.OnlyContain(c => c.Version == version && c.Nacionalidad == "Colombiana");
        drawer.FindComponent<DrawerFormulario>().Instance.HayCambios().Should().BeTrue();
    }

    [Fact]
    public async Task Afiliacion_invalida_se_muestra_en_el_campo_y_no_se_envia()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);
        await IrAPasoFicha(drawer, "Situación");
        await CambiarCampoFicha(drawer, "N.º de afiliación a la Seguridad Social", "ABC");
        await GuardarFicha(drawer);
        drawer.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "N.º de afiliación a la Seguridad Social")
            .Instance.MensajeError.Should().NotBeNullOrWhiteSpace();
        m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().BeEmpty();
    }

    // «Guardar y terminar» desde el primer paso con un error de otro paso: no se envía y el formulario lleva al paso del error.
    [Fact]
    public async Task Guardar_con_un_error_de_otro_paso_lleva_a_ese_paso_y_senala_el_campo()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);
        await IrAPasoFicha(drawer, "Situación");
        await CambiarCampoFicha(drawer, "N.º de afiliación a la Seguridad Social", "ABC");
        await PulsarFicha(drawer, "Atrás");
        await PulsarFicha(drawer, "Atrás");
        PasoActivoFicha(drawer).Should().Be("Lo básico");

        await GuardarFicha(drawer);

        m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().BeEmpty();
        PasoActivoFicha(drawer).Should().Be("Situación");
        drawer.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "N.º de afiliación a la Seguridad Social")
            .Instance.MensajeError.Should().NotBeNullOrWhiteSpace();
    }

    // Un campo del paso en error no deja avanzar; al corregirlo, sí.
    [Fact]
    public async Task Siguiente_no_avanza_con_errores_del_paso_y_avanza_al_corregirlos()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);
        await CambiarCampoFicha(drawer, "Nombre", "");

        await PulsarFicha(drawer, "Siguiente");

        PasoActivoFicha(drawer).Should().Be("Lo básico");
        drawer.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Nombre").Instance.MensajeError.Should().NotBeNullOrWhiteSpace();
        drawer.FindComponent<DrawerFormulario>().Instance.HayErroresDeCampo.Should().BeTrue();

        await CambiarCampoFicha(drawer, "Nombre", "Marta");
        await PulsarFicha(drawer, "Siguiente");

        PasoActivoFicha(drawer).Should().Be("Trabajo");
        drawer.FindComponent<DrawerFormulario>().Instance.HayErroresDeCampo.Should().BeFalse();
        m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().BeEmpty("avanzar de paso no guarda");
    }

    // Un error de otro paso no se enseña ni bloquea cerrar desde el paso que se ve; el indicador deja volver a los pasos vistos.
    [Fact]
    public async Task El_indicador_deja_volver_a_los_pasos_vistos_y_el_atras_conserva_lo_escrito()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);
        await CambiarCampoFicha(drawer, "Nacionalidad", "Colombiana");
        PasoActivoFicha(drawer).Should().Be("Lo básico");
        drawer.FindAll("li.indicador-pasos-item button").Should().BeEmpty("sin pasos vistos no hay a dónde volver");

        await PulsarFicha(drawer, "Siguiente");
        await PulsarFicha(drawer, "Siguiente");
        PasoActivoFicha(drawer).Should().Be("Situación");
        await drawer.FindAll("li.indicador-pasos-item button").First().ClickAsync(new MouseEventArgs());

        PasoActivoFicha(drawer).Should().Be("Lo básico");
        drawer.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Nacionalidad").Instance.Valor.Should().Be("Colombiana");
        m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().BeEmpty();
    }

    // Un error de campo en otro paso no se enseña en el paso que se ve: no marca el formulario con errores ni bloquea cerrarlo.
    [Fact]
    public async Task Un_error_de_otro_paso_no_marca_el_paso_que_se_ve()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);
        await IrAPasoFicha(drawer, "Situación");
        await CambiarCampoFicha(drawer, "N.º de afiliación a la Seguridad Social", "ABC");
        await PulsarFicha(drawer, "Atrás");
        await PulsarFicha(drawer, "Atrás");
        // Desde Lo básico, «Siguiente» solo valida los campos de Lo básico.
        await PulsarFicha(drawer, "Siguiente");
        PasoActivoFicha(drawer).Should().Be("Trabajo");

        await PulsarFicha(drawer, "Atrás");
        await GuardarFicha(drawer);
        PasoActivoFicha(drawer).Should().Be("Situación");
        await PulsarFicha(drawer, "Atrás");
        await PulsarFicha(drawer, "Atrás");

        drawer.FindComponent<DrawerFormulario>().Instance.HayErroresDeCampo.Should().BeFalse("el error es de Situación y se está en Lo básico");
    }

    // Cada código del servidor que apunta a un campo lleva al paso donde está ese campo, desde cualquier otro paso.
    [Theory]
    [InlineData("Trabajador.DocumentoNoValido", "Lo básico", "Situación")]
    [InlineData("Trabajador.DocumentoSinGestion", "Lo básico", "Situación")]
    [InlineData("Trabajador.SinEmpresa", "Trabajo", "Situación")]
    [InlineData("Delegacion.NoEncontrada", "Trabajo", "Situación")]
    [InlineData("Oficio.NoEncontrado", "Trabajo", "Situación")]
    [InlineData("Trabajador.ExtranjeroCobrableSinGestion", "Situación", "Lo básico")]
    public async Task Un_rechazo_del_servidor_sobre_un_campo_lleva_a_su_paso(string codigo, string pasoEsperado, string pasoDeSalida)
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        m.ResultadoFicha = Result.Fallo(Error.Crear(codigo, "No se pudo guardar eso."));
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);
        await IrAPasoFicha(drawer, pasoDeSalida);

        await GuardarFicha(drawer);

        m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().ContainSingle();
        PasoActivoFicha(drawer).Should().Be(pasoEsperado);
        drawer.Markup.Should().Contain("No se pudo guardar eso.");
    }

    // «Siguiente» sobre un paso con error no borra los errores que un «Guardar» anterior dejó en otros pasos.
    [Fact]
    public async Task Siguiente_conserva_los_errores_de_otros_pasos()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);
        await IrAPasoFicha(drawer, "Situación");
        await CambiarCampoFicha(drawer, "N.º de afiliación a la Seguridad Social", "ABC");
        await PulsarFicha(drawer, "Atrás");
        await CambiarCampoFicha(drawer, "Alta en la empresa", "2023-05-02");
        await CambiarCampoFicha(drawer, "Baja", "2023-05-01");
        await PulsarFicha(drawer, "Atrás");

        await GuardarFicha(drawer);
        PasoActivoFicha(drawer).Should().Be("Trabajo");
        await PulsarFicha(drawer, "Siguiente");
        PasoActivoFicha(drawer).Should().Be("Trabajo", "la baja sigue anterior al alta");

        await CambiarCampoFicha(drawer, "Baja", "2023-06-01");
        await PulsarFicha(drawer, "Siguiente");
        PasoActivoFicha(drawer).Should().Be("Situación");
        drawer.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "N.º de afiliación a la Seguridad Social")
            .Instance.MensajeError.Should().NotBeNullOrWhiteSpace("el error de afiliación sigue señalado");
        m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().BeEmpty();
    }

    // Lo que rechaza el servidor sobre un campo (aquí el documento) lleva al paso donde está, con el mensaje a la vista.
    [Fact]
    public async Task Un_rechazo_del_servidor_sobre_el_documento_lleva_a_lo_basico()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        m.ResultadoFicha = Result.Fallo(Error.Crear("Trabajador.DniDuplicado", "Ya existe un trabajador con este documento de identidad."));
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);
        await CambiarCampoFicha(drawer, "Documento de identidad", "AB123456");
        await IrAPasoFicha(drawer, "Situación");

        await GuardarFicha(drawer);

        PasoActivoFicha(drawer).Should().Be("Lo básico");
        drawer.Markup.Should().Contain("Ya existe un trabajador con este documento de identidad.");
        m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().ContainSingle();
    }

    // Una fecha que no existe (o a medias): el campo la entrega como «», como el campo nativo, y no el texto, que cada
    // pantalla interpretaba a su manera. La ficha no guarda mientras el campo la señala: guardar «» borraría la fecha.
    [Theory]
    [InlineData("2023-13-45")]
    [InlineData("1/2/26")]
    [InlineData("15/03")]
    public async Task Una_fecha_ilegible_se_senala_en_su_campo_sin_enviar(string tecleado)
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);
        await IrAPasoFicha(drawer, "Trabajo");
        await CambiarCampoFicha(drawer, "Alta en la empresa", tecleado);

        await GuardarFicha(drawer);

        m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().BeEmpty();
        PasoActivoFicha(drawer).Should().Be("Trabajo");
        var campo = drawer.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Alta en la empresa").Instance;
        campo.Valor.Should().BeEmpty("el texto que no es fecha no llega al formulario");
        campo.MensajeError.Should().Be("Introduce una fecha válida.");
    }

    // El aviso de un «Guardar» rechazado deja de ser cierto en cuanto la fecha vuelve a ser entera (o se vacía).
    [Fact]
    public async Task Corregir_la_fecha_retira_el_aviso_de_que_no_es_una_fecha()
    {
        var id = Guid.NewGuid(); ConTrabajador(id);
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);
        await IrAPasoFicha(drawer, "Trabajo");
        await CambiarCampoFicha(drawer, "Alta en la empresa", "15/03");
        await GuardarFicha(drawer);
        var campo = () => drawer.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Alta en la empresa").Instance;
        campo().MensajeError.Should().Be("Introduce una fecha válida.", "control: el intento rechazado avisa");

        await CambiarCampoFicha(drawer, "Alta en la empresa", "15/03/2023");

        campo().MensajeError.Should().BeNullOrWhiteSpace("la fecha ya es entera y el aviso del intento anterior no es cierto");
    }

    // Una fecha a medias es lo escrito sin guardar aunque el formulario siga con el valor cargado (el campo entrega «»
    // y, sin alta cargada, «» es lo que ya había): Escape tiene que preguntar antes de descartarla.
    [Fact]
    public async Task Una_fecha_a_medias_cuenta_como_cambio_sin_guardar_aunque_el_valor_no_cambie()
    {
        var id = Guid.NewGuid(); ConTrabajador(id);
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);
        await IrAPasoFicha(drawer, "Trabajo");
        drawer.FindComponent<DrawerFormulario>().Instance.HayCambios().Should().BeFalse("control: sin tocar nada no hay nada que descartar");

        await CambiarCampoFicha(drawer, "Alta en la empresa", "15/03");

        drawer.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Alta en la empresa").Instance.Valor
            .Should().BeEmpty("el valor del formulario no ha cambiado: solo el campo sabe que hay texto");
        drawer.FindComponent<DrawerFormulario>().Instance.HayCambios().Should().BeTrue();

        await CambiarCampoFicha(drawer, "Alta en la empresa", "");

        drawer.FindComponent<DrawerFormulario>().Instance.HayCambios().Should().BeFalse("borrado el texto a medias, vuelve a no haber nada");
    }

    // Al dejar el paso, el campo se destruye con su texto. La marca no puede sobrevivirle
    // (al guardar saldría un aviso bajo un campo vacío y válido), y la fecha vuelve a la cargada: el campo había entregado «»
    // y guardar así borraría el alta sin que nadie lo viera.
    [Fact]
    public async Task Dejar_el_paso_con_una_fecha_a_medias_vuelve_a_la_cargada_sin_aviso()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        m.Detalles[id] = m.Detalles[id] with { FechaAltaEmpresa = new DateOnly(2023, 5, 2) };
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);
        await IrAPasoFicha(drawer, "Trabajo");
        await CambiarCampoFicha(drawer, "Alta en la empresa", "15/03");
        // «Siguiente» con la marca puesta registra el aviso del campo: sin él, la aserción de abajo sobre MensajeError no
        // podría fallar aunque CambiarDePaso dejara el error (nunca habría habido uno que dejar).
        await PulsarFicha(drawer, "Siguiente");
        PasoActivoFicha(drawer).Should().Be("Trabajo", "control: la fecha a medias bloquea el paso");
        drawer.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Alta en la empresa").Instance.MensajeError
            .Should().Be("Introduce una fecha válida.", "control: el aviso estaba antes de dejar el paso");
        await PulsarFicha(drawer, "Atrás");

        // Al volver, el campo nuevo emite su estado al crearse y eso también retiraría el aviso: por eso el DOM de vuelta no
        // distingue si CambiarDePaso lo quitó. El estado del formulario se mira antes de volver, con el paso del campo fuera.
        var errores = (Dictionary<string, string>)typeof(EditarFichaTrabajadorDrawer)
            .GetField("_errores", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(drawer.Instance)!;
        errores.Should().NotContainKey(nameof(EditarFichaTrabajadorCommand.FechaAltaEmpresa), "al dejar el paso, su aviso se va con el campo");

        await IrAPasoFicha(drawer, "Trabajo");
        var campo = drawer.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Alta en la empresa").Instance;
        campo.Valor.Should().Be("2023-05-02", "el campo vuelve con la fecha cargada");
        campo.MensajeError.Should().BeNullOrWhiteSpace("la marca no sobrevive al paso y con ella tampoco su aviso");

        await GuardarFicha(drawer);

        m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().ContainSingle()
            .Which.FechaAltaEmpresa.Should().Be(new DateOnly(2023, 5, 2));
    }

    // Vuelve a la última fecha válida del campo, no a la cargada; una corrección ya hecha no se pierde.
    [Fact]
    public async Task Dejar_el_paso_con_una_fecha_a_medias_conserva_la_ultima_corregida()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        m.Detalles[id] = m.Detalles[id] with { FechaAltaEmpresa = new DateOnly(2023, 5, 2) };
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);
        await IrAPasoFicha(drawer, "Trabajo");
        await CambiarCampoFicha(drawer, "Alta en la empresa", "03/06/2024");
        await CambiarCampoFicha(drawer, "Alta en la empresa", "03/0");
        await PulsarFicha(drawer, "Atrás");

        await GuardarFicha(drawer);

        m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().ContainSingle()
            .Which.FechaAltaEmpresa.Should().Be(new DateOnly(2024, 6, 3));
    }

    // Un «Guardar» rechazado con una fecha no válida en el paso visible no lleva a otro paso con error (eso
    // destruiría el campo y su texto): el formulario se queda donde está la fecha.
    [Fact]
    public async Task Un_guardado_rechazado_no_sale_del_paso_con_la_fecha_no_valida()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);
        await IrAPasoFicha(drawer, "Situación");
        await PulsarFicha(drawer, "Atrás");
        await PulsarFicha(drawer, "Atrás");
        // Un error en Lo básico, antes que Trabajo: sin la guarda, el guardado llevaría allí y revertiría la fecha.
        await CambiarCampoFicha(drawer, "Nombre", "");
        await drawer.FindAll("button.indicador-pasos-boton").Single(b => b.TextContent.Contains("Trabajo")).ClickAsync(new MouseEventArgs());
        PasoActivoFicha(drawer).Should().Be("Trabajo", "control: el indicador lleva al paso ya visto");
        await CambiarCampoFicha(drawer, "Alta en la empresa", "15/03");

        await GuardarFicha(drawer);

        m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().BeEmpty();
        PasoActivoFicha(drawer).Should().Be("Trabajo");
        drawer.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Alta en la empresa")
            .Instance.MensajeError.Should().Be("Introduce una fecha válida.");
    }

    // Tras un texto no válido, vaciar el campo tiene que dejar quitar la Baja.
    [Fact]
    public async Task Tras_un_texto_no_valido_vaciar_la_baja_la_quita()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        m.Detalles[id] = m.Detalles[id] with { FechaAltaEmpresa = new DateOnly(2023, 5, 2), FechaBajaEmpresa = new DateOnly(2024, 1, 31) };
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);
        await IrAPasoFicha(drawer, "Trabajo");
        await CambiarCampoFicha(drawer, "Baja", "31/02/2024");

        await CambiarCampoFicha(drawer, "Baja", "");
        await GuardarFicha(drawer);

        m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().ContainSingle()
            .Which.FechaBajaEmpresa.Should().BeNull("vaciar el campo quita la baja");
    }

    // Sin contacto de emergencia visible la edición tiene tres pasos; con él, el paso Contacto va antes de Situación.
    [Fact]
    public async Task Los_pasos_son_los_del_alta_mas_situacion_y_contacto_solo_si_el_servidor_lo_envia()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);
        drawer.FindAll("ol.indicador-pasos li .indicador-pasos-titulo").Select(t => t.TextContent.Trim())
            .Should().Equal("Lo básico", "Trabajo", "Situación");
        drawer.Find("ol.indicador-pasos").GetAttribute("aria-label").Should().Be("Edición de trabajador");
        await PulsarFicha(drawer, "Cancelar");

        m.Detalles[id] = m.Detalles[id] with { ContactoEmergenciaVisible = true };
        drawer = await AbrirEditorFicha(cut);
        drawer.FindAll("ol.indicador-pasos li .indicador-pasos-titulo").Select(t => t.TextContent.Trim())
            .Should().Equal("Lo básico", "Trabajo", "Contacto", "Situación");
        await IrAPasoFicha(drawer, "Contacto");
        EtiquetasDelPasoFicha(drawer).Should().Equal("Contacto de emergencia", "Teléfono de emergencia");
    }

    // Reabrir el drawer vuelve al primer paso, sin pasos vistos ni errores del intento anterior.
    [Fact]
    public async Task Reabrir_empieza_en_el_primer_paso()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);
        await PulsarFicha(drawer, "Siguiente");
        await PulsarFicha(drawer, "Siguiente");
        PasoActivoFicha(drawer).Should().Be("Situación");
        // Se cierra desde un paso posterior (la X), no desde el primero.
        await drawer.Find("button.drawer-cerrar").ClickAsync(new MouseEventArgs());

        drawer = await AbrirEditorFicha(cut);

        PasoActivoFicha(drawer).Should().Be("Lo básico");
        drawer.FindAll("li.indicador-pasos-item button").Should().BeEmpty();
    }

    [Fact]
    public async Task La_baja_anterior_al_alta_se_senala_en_su_campo_sin_enviar()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);
        await IrAPasoFicha(drawer, "Trabajo");
        await CambiarCampoFicha(drawer, "Alta en la empresa", "2023-05-02");
        await CambiarCampoFicha(drawer, "Baja", "2023-05-01");
        await PulsarFicha(drawer, "Siguiente");
        PasoActivoFicha(drawer).Should().Be("Trabajo", "la baja anterior al alta bloquea el paso");
        await GuardarFicha(drawer);
        drawer.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Baja")
            .Instance.MensajeError.Should().NotBeNullOrWhiteSpace();
        m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task Reabrir_el_drawer_recarga_la_version_y_los_datos_del_servidor()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);
        await drawer.FindAll("button").Single(b => b.TextContent.Trim() == "Cancelar").ClickAsync(new MouseEventArgs());
        var version = Guid.NewGuid();
        m.Detalles[id] = m.Detalles[id] with { Version = version, Nacionalidad = "Portuguesa" };
        drawer = await AbrirEditorFicha(cut);
        drawer.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Nacionalidad").Instance.Valor.Should().Be("Portuguesa");
        await GuardarFicha(drawer);
        m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().ContainSingle().Which.Version.Should().Be(version);
    }

    [Fact]
    public async Task La_ficha_no_disponible_no_permite_guardar()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        var cut = Renderizar(id); await AbrirPestanaAsync(cut, "Ficha");
        m.Detalles.Remove(id);
        await cut.Find("[data-caja='altas-plataforma'] button").ClickAsync(new MouseEventArgs());
        var drawer = cut.FindComponent<EditarFichaTrabajadorDrawer>();
        drawer.FindComponent<DrawerFormulario>().Instance.MensajeError.Should().NotBeNullOrWhiteSpace();
        drawer.FindAll("button").Single(b => b.TextContent.Trim() == "Guardar").HasAttribute("disabled").Should().BeTrue();
        m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task Consulta_ve_las_cajas_vacias_sin_edicion_ni_drawer()
    {
        this.ConRolDeEscritura(Roles.Consulta);
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        m.Detalles[id] = m.Detalles[id] with { Puesto = null };
        var cut = Renderizar(id); await AbrirPestanaAsync(cut, "Ficha");
        cut.FindAll("[data-caja] dd").Should().HaveCount(11).And.OnlyContain(d => d.TextContent.Trim() == "—");
        cut.FindAll("[data-caja] button").Should().BeEmpty();
        cut.FindComponents<EditarFichaTrabajadorDrawer>().Should().BeEmpty();
    }

    [Fact]
    public async Task Una_carga_tardia_de_otro_trabajador_no_sustituye_el_borrador_actual()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var m = ConTrabajador(a);
        m.Detalles[b] = Detalle(b, "Otra", "Persona") with { Nacionalidad = "Portuguesa" };
        var retenida = new TaskCompletionSource();
        m.Retener = q => q is ObtenerTrabajadorPorIdQuery { Id: var id } && id == a ? retenida.Task : null;
        var drawer = Render<EditarFichaTrabajadorDrawer>(p => p.Add(x => x.TrabajadorId, a).Add(x => x.Visible, true));
        await drawer.InvokeAsync(() => drawer.Render(p => p.Add(x => x.TrabajadorId, b)));
        await drawer.InvokeAsync(() => retenida.SetResult());
        drawer.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Nacionalidad").Instance.Valor.Should().Be("Portuguesa");
        await GuardarFicha(drawer);
        m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().ContainSingle().Which.Id.Should().Be(b);
    }

    [Theory]
    [InlineData("41123456789", "41 1234567 89")]
    [InlineData("281234567840", "28 12345678 40")]
    [InlineData(null, null)]
    public void La_afiliacion_se_presenta_agrupada_como_en_la_maqueta(string? guardado, string? presentado) =>
        TrabajadorDetalle.FormatoAfiliacion(guardado).Should().Be(presentado);
}
