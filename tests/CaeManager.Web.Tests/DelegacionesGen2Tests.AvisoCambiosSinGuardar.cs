using Bunit;
using CaeManager.Application.Tenants.Commands.AbrirAccesoSoporte;
using CaeManager.Application.Tenants.Commands.CrearClienteDelegante;
using CaeManager.Application.Tenants.Commands.CrearOperadorCaeExterno;
using CaeManager.Application.Tenants.Queries.AutorizarOperadorCaeExterno;
using CaeManager.Domain.Common;
using CaeManager.Web.Features.Delegaciones.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// P1-E2b: salir de /delegaciones con uno de sus modales a medias pregunta antes: el
/// acceso de Soporte TALVEG, la nueva delegación, la autorización de un Operador CAE
/// externo y el alta del panel de Operadores CAE externos. Lo preseleccionado (el
/// Operador CAE que trae el enlace, los valores por defecto del acceso) no es un cambio,
/// y lo ya creado no deja nada que perder. Solo el aviso: ni la apertura del acceso ni la
/// autorización cambian.
/// </summary>
public partial class DelegacionesGen2Tests
{
    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();

    private static async Task EscribirEnElModalAsync(IRenderedComponent<Delegaciones> cut, string valor)
    {
        var campo = cut.Find("[role=dialog] input");
        await campo.InputAsync(new ChangeEventArgs { Value = valor });
        await campo.BlurAsync(new FocusEventArgs());
    }

    [Fact]
    public async Task Aviso_el_acceso_de_Soporte_TALVEG_recien_abierto_no_pregunta_y_con_motivo_si()
    {
        var (cut, _, _) = Renderizar(esAdministradorPlataforma: false, Delegacion(soporte: true, activa: false));
        await BotonConTexto(cut, "Abrir acceso").ClickAsync(new MouseEventArgs());
        cut.FindAll("[role=dialog]").Should().NotBeEmpty("el test necesita el modal de acceso abierto");

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "horas y permisos por defecto no son un cambio");

        await EscribirEnElModalAsync(cut, "Incidencia de importación");

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    [Fact]
    public async Task Aviso_la_nueva_delegacion_con_nombre_pregunta_y_creada_ya_no()
    {
        var (cut, mediador, _) = Renderizar(Delegacion());
        await BotonConTexto(cut, "Nueva delegación").ClickAsync(new MouseEventArgs());
        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "sin nombre no hay nada que perder");

        await EscribirEnElModalAsync(cut, "Organización nueva");
        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
        await cut.PulsarEnElAvisoAsync("Seguir editando");

        await BotonConTexto(cut, "Crear").ClickAsync(new MouseEventArgs());
        mediador.Enviadas.Should().Contain(x => x.Peticion is CrearClienteDeleganteCommand, "si no se creó, el test no mide nada");

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "la delegación ya está creada");
    }

    // ------------------------------------------------- ModalFormulario (S12, lote 3b): el error del formulario va en el aviso fijo

    private static AngleSharp.Dom.IElement BotonDelModal(IRenderedComponent<Delegaciones> cut, string texto) =>
        cut.FindAll("[role=dialog] .modal-pie button").Single(b => b.TextContent.Trim() == texto);

    [Fact]
    public async Task El_error_de_validacion_del_acceso_de_Soporte_sale_en_el_aviso_fijo_y_escribir_de_nuevo_lo_retira()
    {
        var (cut, mediador, _) = Renderizar(esAdministradorPlataforma: false, Delegacion(soporte: true, activa: false));
        await BotonConTexto(cut, "Abrir acceso").ClickAsync(new MouseEventArgs());
        var horas = cut.FindAll("[role=dialog] input")[1];
        await horas.InputAsync(new ChangeEventArgs { Value = "no-es-un-numero" });
        await horas.BlurAsync(new FocusEventArgs());

        await BotonDelModal(cut, "Abrir acceso").ClickAsync(new MouseEventArgs());

        cut.Find(".modal-aviso .alerta-formulario").TextContent.Should().Contain("Indica las horas de acceso como un número.");
        cut.FindAll(".modal-cuerpo .alerta-formulario").Should().BeEmpty("el aviso va fuera del cuerpo desplazable (D-20)");
        cut.FindAll("[role=dialog]").Should().NotBeEmpty("el error no cierra el modal ni tira lo escrito");
        mediador.Enviadas.Should().NotContain(x => x.Peticion is AbrirAccesoSoporteCommand, "las horas no son un número: no sale ningún comando");

        var motivo = cut.FindAll("[role=dialog] input")[0];
        await motivo.InputAsync(new ChangeEventArgs { Value = "Incidencia de importación" });
        await motivo.BlurAsync(new FocusEventArgs());

        cut.FindAll(".modal-aviso").Should().BeEmpty("escribir de nuevo retira el error anterior");
    }

    [Fact]
    public async Task El_rechazo_de_Nueva_delegacion_sale_en_el_aviso_fijo_y_lo_escrito_no_se_pierde()
    {
        var (cut, mediador, _) = Renderizar(Delegacion());
        mediador.ResultadoCreacion = Result.Fallo<Guid>(Error.Crear("Cliente.NombreEnUso", "Ya existe una organización con ese nombre."));
        await BotonConTexto(cut, "Nueva delegación").ClickAsync(new MouseEventArgs());
        await EscribirEnElModalAsync(cut, "Organización nueva");

        await BotonDelModal(cut, "Crear").ClickAsync(new MouseEventArgs());

        cut.Find(".modal-aviso .alerta-formulario").TextContent.Should().Contain("Ya existe una organización con ese nombre.");
        cut.FindAll(".modal-cuerpo .alerta-formulario").Should().BeEmpty();
        cut.FindComponents<CaeManager.Web.Components.DesignSystem.CampoTexto>()
            .Single(c => c.Instance.Etiqueta == "Nombre de la organización").Instance.Valor.Should().Be("Organización nueva", "el rechazo no tira lo escrito");

        await EscribirEnElModalAsync(cut, "Organización nueva 2");

        cut.FindAll(".modal-aviso").Should().BeEmpty("escribir de nuevo retira el error anterior");
    }

    [Fact]
    public async Task El_rechazo_de_Autorizar_un_Operador_sale_en_el_aviso_fijo_y_el_primario_es_unico()
    {
        var arcoSpa = new OperadorCaeExternoAutorizableDto(Guid.NewGuid(), "ArcoSPA");
        ComoAdministradorDelTenantPropietario(_ => arcoSpa);
        var (cut, mediador, _) = Renderizar(esAdministradorPlataforma: false);
        mediador.ResultadoAutorizacion = Result.Fallo<Guid>(Error.Crear("DelegacionTenant.OtroOperadorVigente",
            "Tu organización ya tiene otro Operador CAE externo activo."));
        await BotonConTexto(cut, "Autorizar un Operador CAE externo").ClickAsync(new MouseEventArgs());
        await EscribirEnElModalAsync(cut, "ArcoSPA");
        await cut.Find("[role=option]").ClickAsync(new MouseEventArgs());

        await BotonDelModal(cut, "Autorizar").ClickAsync(new MouseEventArgs());

        cut.Find(".modal-aviso .alerta-formulario").TextContent.Should().Contain("ya tiene otro Operador CAE externo activo");
        cut.FindAll(".modal-cuerpo .alerta-formulario").Should().BeEmpty();
        cut.FindAll("[role=dialog] .modal-pie button").Count(b => b.ClassList.Contains("boton-primario")).Should().Be(1);
    }

    private static bool PreguntaDescartar(IRenderedComponent<Delegaciones> cut) =>
        cut.FindAll("h2").Any(h => h.TextContent.Trim() == "¿Descartar cambios?");

    /// <summary>
    /// D-05: «Cancelar» de los modales de /delegaciones (acceso de Soporte TALVEG, nueva delegación, alta de Operador CAE
    /// externo) cierra como la X: con algo escrito pregunta «¿Descartar cambios?», sin nada cierra directamente.
    /// </summary>
    [Fact]
    public async Task Cancelar_el_acceso_de_Soporte_TALVEG_con_motivo_pregunta_y_sin_motivo_cierra()
    {
        var (cut, _, _) = Renderizar(esAdministradorPlataforma: false, Delegacion(soporte: true, activa: false));
        await BotonConTexto(cut, "Abrir acceso").ClickAsync(new MouseEventArgs());
        await BotonConTexto(cut, "Cancelar").ClickAsync(new MouseEventArgs());
        cut.FindAll("[role=dialog]").Should().BeEmpty("horas y permisos por defecto no son un cambio: Cancelar cierra");
        PreguntaDescartar(cut).Should().BeFalse();

        await BotonConTexto(cut, "Abrir acceso").ClickAsync(new MouseEventArgs());
        await EscribirEnElModalAsync(cut, "Incidencia de importación");
        await BotonConTexto(cut, "Cancelar").ClickAsync(new MouseEventArgs());

        PreguntaDescartar(cut).Should().BeTrue("con el motivo escrito, Cancelar pregunta como la X");
        await BotonConTexto(cut, "Descartar cambios").ClickAsync(new MouseEventArgs());
        cut.FindAll("[role=dialog]").Should().BeEmpty();
    }

    [Fact]
    public async Task Cancelar_la_nueva_delegacion_con_nombre_pregunta_y_sin_nombre_cierra()
    {
        var (cut, _, _) = Renderizar(Delegacion());
        await BotonConTexto(cut, "Nueva delegación").ClickAsync(new MouseEventArgs());
        await BotonConTexto(cut, "Cancelar").ClickAsync(new MouseEventArgs());
        cut.FindAll("[role=dialog]").Should().BeEmpty("sin nombre no hay nada que perder");
        PreguntaDescartar(cut).Should().BeFalse();

        await BotonConTexto(cut, "Nueva delegación").ClickAsync(new MouseEventArgs());
        await EscribirEnElModalAsync(cut, "Organización nueva");
        await BotonConTexto(cut, "Cancelar").ClickAsync(new MouseEventArgs());

        PreguntaDescartar(cut).Should().BeTrue("con el nombre escrito, Cancelar pregunta como la X");
        await BotonConTexto(cut, "Descartar cambios").ClickAsync(new MouseEventArgs());
        cut.FindAll("[role=dialog]").Should().BeEmpty();
    }

    [Fact]
    public async Task Cancelar_el_alta_de_Operador_CAE_externo_con_nombre_pregunta_y_sin_nombre_cierra()
    {
        var (cut, _, _) = Renderizar(esAdministradorPlataforma: true);
        await BotonConTexto(cut, "Nuevo Operador CAE externo").ClickAsync(new MouseEventArgs());
        await BotonConTexto(cut, "Cancelar").ClickAsync(new MouseEventArgs());
        cut.FindAll("[role=dialog]").Should().BeEmpty("sin nombre no hay nada que perder");
        PreguntaDescartar(cut).Should().BeFalse();

        await BotonConTexto(cut, "Nuevo Operador CAE externo").ClickAsync(new MouseEventArgs());
        await EscribirEnElModalAsync(cut, "ArcoSPA");
        await BotonConTexto(cut, "Cancelar").ClickAsync(new MouseEventArgs());

        PreguntaDescartar(cut).Should().BeTrue("con el nombre escrito, Cancelar pregunta como la X");
        cut.FindAll("[role=dialog]").Should().NotBeEmpty("el alta sigue abierta hasta que se confirme");
        await BotonConTexto(cut, "Descartar cambios").ClickAsync(new MouseEventArgs());
        cut.FindAll("[role=dialog]").Should().BeEmpty();
    }

    /// <summary>D-05 (hallazgo de la revisión puente): «Cancelar» del modal «Autorizar un Operador CAE externo» cierra como la X.</summary>
    [Fact]
    public async Task Cancelar_autorizar_un_Operador_con_la_busqueda_escrita_pregunta_y_sin_tocar_cierra()
    {
        var arcoSpa = new OperadorCaeExternoAutorizableDto(Guid.NewGuid(), "ArcoSPA");
        ComoAdministradorDelTenantPropietario(q => q.OperadorId == arcoSpa.TenantId ? arcoSpa : null);
        var (cut, _, _) = Renderizar(esAdministradorPlataforma: false);
        await BotonConTexto(cut, "Autorizar un Operador CAE externo").ClickAsync(new MouseEventArgs());
        await BotonConTexto(cut, "Cancelar").ClickAsync(new MouseEventArgs());
        cut.FindAll("[role=dialog]").Should().BeEmpty("sin nada escrito, Cancelar cierra directamente");
        PreguntaDescartar(cut).Should().BeFalse();

        await BotonConTexto(cut, "Autorizar un Operador CAE externo").ClickAsync(new MouseEventArgs());
        await EscribirEnElModalAsync(cut, "Arco");
        await BotonConTexto(cut, "Cancelar").ClickAsync(new MouseEventArgs());

        PreguntaDescartar(cut).Should().BeTrue("con la búsqueda escrita, Cancelar pregunta como la X");
        cut.FindAll("[role=dialog]").Should().NotBeEmpty("hasta que se confirme, el modal sigue abierto");
        await BotonConTexto(cut, "Descartar cambios").ClickAsync(new MouseEventArgs());
        cut.FindAll("[role=dialog]").Should().BeEmpty();
    }

    [Fact]
    public async Task Aviso_el_Operador_CAE_que_trae_el_enlace_no_es_un_cambio_y_buscar_otro_si()
    {
        var arcoSpa = new OperadorCaeExternoAutorizableDto(Guid.NewGuid(), "ArcoSPA");
        ComoAdministradorDelTenantPropietario(q => q.OperadorId == arcoSpa.TenantId ? arcoSpa : null);
        _urlInicial = $"delegaciones?autorizar={arcoSpa.TenantId}";
        var (cut, _, _) = Renderizar(esAdministradorPlataforma: false);
        cut.WaitForAssertion(() => cut.Find("[role=option]").GetAttribute("aria-selected").Should().Be("true"));

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "la preselección del enlace no es un cambio de quien autoriza");

        await BotonConTexto(cut, "Autorizar un Operador CAE externo").ClickAsync(new MouseEventArgs());
        await EscribirEnElModalAsync(cut, "Arco");

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    [Fact]
    public async Task Aviso_el_alta_de_Operador_CAE_externo_con_nombre_pregunta_y_creada_ya_no()
    {
        var (cut, mediador, _) = Renderizar(esAdministradorPlataforma: true);
        await BotonConTexto(cut, "Nuevo Operador CAE externo").ClickAsync(new MouseEventArgs());
        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "sin nombre no hay nada que perder");

        await EscribirEnElModalAsync(cut, "ArcoSPA");
        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
        await cut.PulsarEnElAvisoAsync("Seguir editando");

        await BotonConTexto(cut, "Crear").ClickAsync(new MouseEventArgs());
        mediador.Enviadas.Should().Contain(x => x.Peticion is CrearOperadorCaeExternoCommand, "si no se creó, el test no mide nada");

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "el Operador CAE externo ya está creado");
    }

    /// <summary>
    /// FS-22: el correo y el nombre del primer Administrador también son trabajo escrito. Cada
    /// uno por separado, con el nombre del Operador CAE externo vacío: si el aviso solo mirase
    /// el nombre, ninguno de los dos preguntaría.
    /// </summary>
    [Theory]
    [InlineData("Correo del primer Administrador", "marta@operador-sur.test")]
    [InlineData("Nombre del primer Administrador", "Marta Ruiz")]
    public async Task Aviso_el_alta_de_Operador_CAE_externo_solo_con_un_dato_del_primer_Administrador_pregunta(string etiqueta, string valor)
    {
        var (cut, _, _) = Renderizar(esAdministradorPlataforma: true);
        await BotonConTexto(cut, "Nuevo Operador CAE externo").ClickAsync(new MouseEventArgs());
        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "control: recién abierto no hay nada que perder");

        await EscribirEnElCampoAsync(cut, etiqueta, valor);

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }
}
