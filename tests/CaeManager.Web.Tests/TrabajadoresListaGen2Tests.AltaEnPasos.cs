using Bunit;
using CaeManager.Application.Trabajadores.Commands.CrearTrabajador;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Trabajadores.Components;
using CaeManager.Web.Features.Trabajadores.Pages;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// El alta de Trabajador en el panel de la maqueta «Panel Formulario TALVEG»: tres pasos (Lo básico, Trabajo, Contacto) que
/// ordenan los mismos campos de siempre. Se fija lo que el panel tenía en main (campos, acciones, un solo comando al guardar)
/// para que el reparto en pasos no pierda ni cambie nada: solo estructura. Quién puede abrir el alta (el rol Consulta no ve
/// «+ Nuevo trabajador») lo fijan <c>TrabajadoresListaGen2Tests.PorRol</c>.
/// </summary>
public partial class TrabajadoresListaGen2Tests
{
    private static IRenderedComponent<DrawerFormulario> PanelDeAlta(IRenderedComponent<Trabajadores> cut) =>
        cut.FindComponents<DrawerFormulario>().Single(d => d.Instance.Titulo == "Nuevo trabajador");

    private static string[] BotonesDelPanel(IRenderedComponent<Trabajadores> cut) =>
        cut.FindAll(".drawer-pie button").Select(b => b.TextContent.Trim()).ToArray();

    private static string[] PasosDelPanel(IRenderedComponent<Trabajadores> cut) =>
        cut.FindAll(".drawer-pasos .indicador-pasos-titulo").Select(t => t.TextContent.Trim()).ToArray();

    private static Task PulsarEnElPanelAsync(IRenderedComponent<Trabajadores> cut, string texto) =>
        cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == texto).ClickAsync(new MouseEventArgs());

    [Fact]
    public async Task Alta_en_pasos_reparte_los_campos_de_main_sin_perder_ninguno()
    {
        var cut = Renderizar(new MediatorFalso());
        await AbrirAltaAsync(cut);

        cut.Find(".drawer-panel h2").TextContent.Trim().Should().Be("Nuevo trabajador");
        PasosDelPanel(cut).Should().Equal("Lo básico", "Trabajo", "Contacto");

        // Paso 1, Lo básico: empleador (radio + selector), documento, nombre, apellidos, alias y fecha de nacimiento.
        cut.FindAll(".drawer-panel input[name='tipo-empleador']").Should().HaveCount(2);
        PanelDeAlta(cut).FindComponents<CampoSelect>().Should().ContainSingle();
        PanelDeAlta(cut).FindComponents<CampoTexto>().Should().HaveCount(5);
        BotonesDelPanel(cut).Should().Equal("Cancelar", "Guardar y terminar", "Siguiente");

        // Paso 2, Trabajo: Oficio y Delegación (en el sitio del Puesto, para un Trabajador de Empresa), tipo de contratación y
        // observaciones (se llega con el paso 1 completo).
        await cut.Find(".drawer-panel select").ChangeAsync(new ChangeEventArgs { Value = EmpresaEbro.ToString() });
        await EscribirDatosCompletosDelAltaAsync(cut);
        await PulsarEnElPanelAsync(cut, "Siguiente");
        PanelDeAlta(cut).FindComponents<SelectorDelegacionEmpresaYOficio>().Should().ContainSingle();
        PanelDeAlta(cut).FindComponents<CampoTexto>().Should().BeEmpty("el Puesto en texto es solo del Trabajador de Subcontrata");
        PanelDeAlta(cut).FindComponents<CampoSelect>().Should().HaveCount(2, "la Delegación y el tipo de contratación");
        PanelDeAlta(cut).FindComponents<CampoTextarea>().Should().ContainSingle();
        BotonesDelPanel(cut).Should().Equal("Atrás", "Guardar y terminar", "Siguiente");

        // Paso 3, Contacto: correo y teléfono. El último paso termina con «Guardar».
        await PulsarEnElPanelAsync(cut, "Siguiente");
        PanelDeAlta(cut).FindComponents<CampoTexto>().Select(c => c.Instance.Type).Should().Equal("email", "tel");
        BotonesDelPanel(cut).Should().Equal("Atrás", "Guardar");
        cut.FindAll(".drawer-pie .boton-primario").Should().ContainSingle().Which.TextContent.Trim().Should().Be("Guardar");

        // Atrás vuelve sin perder el paso anterior; la X y Cancelar siguen preguntando (guardián del kit).
        await PulsarEnElPanelAsync(cut, "Atrás");
        PasosDelPanel(cut).Should().Equal("Lo básico", "Trabajo", "Contacto");
        BotonesDelPanel(cut).Should().Equal("Atrás", "Guardar y terminar", "Siguiente");
    }

    [Fact]
    public async Task Guardar_en_el_ultimo_paso_envia_un_unico_comando_con_lo_escrito_en_todos_los_pasos()
    {
        var mediador = new MediatorFalso();
        var cut = Renderizar(mediador);
        await AbrirAltaAsync(cut);
        await cut.Find(".drawer-panel select").ChangeAsync(new ChangeEventArgs { Value = EmpresaEbro.ToString() });
        await EscribirDatosCompletosDelAltaAsync(cut);
        await PulsarEnElPanelAsync(cut, "Siguiente");
        await PanelDeAlta(cut).FindComponents<CampoTextarea>().Single().Find("textarea").InputAsync(new ChangeEventArgs { Value = "Turno de noche" });
        await PulsarEnElPanelAsync(cut, "Siguiente");
        await PanelDeAlta(cut).FindComponents<CampoTexto>().Single(c => c.Instance.Type == "email").Find("input")
            .InputAsync(new ChangeEventArgs { Value = "carla@ebro.example" });

        mediador.Enviadas.OfType<CrearTrabajadorCommand>().Should().BeEmpty("«Siguiente» no guarda");
        await PulsarEnElPanelAsync(cut, "Guardar");

        var enviada = mediador.Enviadas.OfType<CrearTrabajadorCommand>().Should().ContainSingle("el alta sigue siendo un solo comando").Subject;
        enviada.Nombre.Should().Be("Carla");
        enviada.Observaciones.Should().Be("Turno de noche", "lo escrito en el paso 2 viaja en el mismo comando");
        enviada.Email.Should().Be("carla@ebro.example", "lo escrito en el paso 3 también");
    }

    [Fact]
    public async Task Guardar_y_terminar_desde_el_primer_paso_guarda_lo_escrito_hasta_ahi()
    {
        var mediador = new MediatorFalso();
        var cut = Renderizar(mediador);
        await AbrirAltaAsync(cut);
        await cut.Find(".drawer-panel select").ChangeAsync(new ChangeEventArgs { Value = EmpresaEbro.ToString() });
        await EscribirDatosCompletosDelAltaAsync(cut);

        await PulsarEnElPanelAsync(cut, "Guardar y terminar");

        mediador.Enviadas.OfType<CrearTrabajadorCommand>().Should().ContainSingle();
        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel").Should().BeEmpty());
    }

    private static async Task LlegarAlUltimoPasoAsync(IRenderedComponent<Trabajadores> cut)
    {
        await cut.Find(".drawer-panel select").ChangeAsync(new ChangeEventArgs { Value = EmpresaEbro.ToString() });
        await EscribirDatosCompletosDelAltaAsync(cut);
        await PulsarEnElPanelAsync(cut, "Siguiente");
        await PulsarEnElPanelAsync(cut, "Siguiente");
        PasosDelPanel(cut).Should().HaveCount(3);
        BotonesDelPanel(cut).Should().Equal("Atrás", "Guardar");
    }

    private static Func<object, Task<object>?> FallaConErrores(params (string Campo, string Mensaje)[] errores) =>
        p => p is CrearTrabajadorCommand
            ? Task.FromException<object>(new ValidationException(errores.Select(e => new ValidationFailure(e.Campo, e.Mensaje))))
            : null;

    [Fact]
    public async Task Siguiente_sin_empleador_no_avanza_ni_marca_el_paso_como_visto()
    {
        var cut = Renderizar(new MediatorFalso());
        await AbrirAltaAsync(cut);
        await EscribirDatosCompletosDelAltaAsync(cut);

        await PulsarEnElPanelAsync(cut, "Siguiente");

        cut.Find(".drawer-panel .alerta-formulario").TextContent.Trim().Should().Be("Selecciona una empresa.");
        cut.FindAll(".drawer-panel input[name='tipo-empleador']").Should().HaveCount(2, "sigue en Lo básico, donde está el empleador");
        cut.FindAll(".indicador-pasos-completado").Should().BeEmpty("el paso no se da por visto mientras no está bien");
    }

    [Fact]
    public async Task Siguiente_con_un_campo_obligatorio_del_paso_vacio_no_avanza_y_enseña_el_error_en_el_campo()
    {
        _validadorReal = true;
        var cut = Renderizar(new MediatorFalso());
        await AbrirAltaAsync(cut);
        await cut.Find(".drawer-panel select").ChangeAsync(new ChangeEventArgs { Value = EmpresaEbro.ToString() });

        await PulsarEnElPanelAsync(cut, "Siguiente");

        cut.FindAll(".drawer-panel input[name='tipo-empleador']").Should().HaveCount(2, "no pasa a Trabajo con el documento, el nombre y los apellidos en blanco");
        cut.FindAll(".drawer-panel .campo-mensaje-error").Should().NotBeEmpty("cada campo del paso en error lo dice junto a sí");
        PanelDeAlta(cut).Instance.HayErroresDeCampo.Should().BeTrue();
        BotonesDelPanel(cut).Should().Equal("Cancelar", "Guardar y terminar", "Siguiente");
    }

    [Fact]
    public async Task Un_error_de_validacion_del_servidor_devuelve_al_primer_paso_con_error()
    {
        var mediador = new MediatorFalso { Retener = FallaConErrores(("Dni", "El documento no es válido."), ("Email", "El correo no es válido.")) };
        var cut = Renderizar(mediador);
        await AbrirAltaAsync(cut);
        await LlegarAlUltimoPasoAsync(cut);

        await PulsarEnElPanelAsync(cut, "Guardar");

        cut.FindAll(".drawer-panel input[name='tipo-empleador']").Should().HaveCount(2, "el documento está en Lo básico, que es el primer paso con error");
        cut.FindAll(".drawer-panel .campo-mensaje-error").Select(e => e.TextContent.Trim()).Should().Contain("El documento no es válido.");
    }

    [Fact]
    public async Task Un_error_de_un_paso_oculto_no_bloquea_el_cierre_por_fuera_hasta_que_se_ve()
    {
        var mediador = new MediatorFalso { Retener = FallaConErrores(("Email", "El correo no es válido.")) };
        var cut = Renderizar(mediador);
        await AbrirAltaAsync(cut);
        await LlegarAlUltimoPasoAsync(cut);

        await PulsarEnElPanelAsync(cut, "Guardar");
        PanelDeAlta(cut).Instance.HayErroresDeCampo.Should().BeTrue("el error es del paso que se está viendo");

        await PulsarEnElPanelAsync(cut, "Atrás");
        PanelDeAlta(cut).Instance.HayErroresDeCampo.Should().BeFalse(
            "en Trabajo no se ve ningún error: Escape y el clic fuera no pueden quedar bloqueados por uno que nadie ve");

        await PulsarEnElPanelAsync(cut, "Siguiente");
        PanelDeAlta(cut).Instance.HayErroresDeCampo.Should().BeTrue("al volver a Contacto el error sigue ahí, a la vista");
    }

    [Fact]
    public async Task Un_fallo_de_regla_de_negocio_como_el_documento_duplicado_devuelve_al_primer_paso()
    {
        var mediador = new MediatorFalso
        {
            Retener = p => p is CrearTrabajadorCommand
                ? Task.FromResult<object>(Result.Fallo<Guid>(Error.Crear("Trabajador.DniDuplicado", "Ya existe un trabajador con este DNI.")))
                : null,
        };
        var cut = Renderizar(mediador);
        await AbrirAltaAsync(cut);
        await LlegarAlUltimoPasoAsync(cut);

        await PulsarEnElPanelAsync(cut, "Guardar");

        cut.Find(".drawer-panel .alerta-formulario").TextContent.Trim().Should().Be("Ya existe un trabajador con este DNI.");
        cut.FindAll(".drawer-panel input[name='tipo-empleador']").Should().HaveCount(2, "el documento se corrige en Lo básico");
    }

    [Fact]
    public async Task Mientras_se_guarda_ni_el_indicador_ni_el_pie_dejan_cambiar_de_paso()
    {
        var respuesta = new TaskCompletionSource<object>();
        var mediador = new MediatorFalso { Retener = p => p is CrearTrabajadorCommand ? respuesta.Task : null };
        var cut = Renderizar(mediador);
        await AbrirAltaAsync(cut);
        await cut.Find(".drawer-panel select").ChangeAsync(new ChangeEventArgs { Value = EmpresaEbro.ToString() });
        await EscribirDatosCompletosDelAltaAsync(cut);
        await PulsarEnElPanelAsync(cut, "Siguiente");

        var guardado = cut.InvokeAsync(() => PanelDeAlta(cut).Instance.OnGuardar.InvokeAsync());

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".drawer-pie button").Should().OnlyContain(b => b.HasAttribute("disabled"), "Atrás, Guardar y terminar y Siguiente esperan al guardado");
            cut.FindAll(".drawer-pasos button").Should().BeEmpty("el indicador no ofrece volver a un paso con el alta en vuelo");
        });

        await cut.InvokeAsync(() => respuesta.SetResult(Result.Exito(Guid.NewGuid())));
        await guardado;
    }

    [Fact]
    public async Task Cancelar_en_el_primer_paso_con_datos_pregunta_si_descartar()
    {
        var cut = Renderizar(new MediatorFalso());
        await AbrirAltaAsync(cut);
        await EscribirDocumentoAsync(cut, "12345678Z");

        await PulsarEnElPanelAsync(cut, "Cancelar");

        cut.FindAll("h2").Should().Contain(h => h.TextContent.Trim() == "¿Descartar cambios?");
        cut.FindAll(".drawer-panel").Should().NotBeEmpty("el panel sigue abierto hasta que se confirme");
    }
}
