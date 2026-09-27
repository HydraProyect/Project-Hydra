using Bunit;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using RolesIdentidad = CaeManager.Infrastructure.Identity.Roles;

namespace CaeManager.Web.Tests;

/// <summary>
/// P1-E2b: salir de /usuarios con el alta o la edición de Usuario a medias, o con un
/// Gestor CAE de destino ya elegido en la desactivación con traspaso de cartera, pregunta
/// antes. La ficha recién cargada no es un cambio, y lo ya guardado no deja nada que perder.
/// </summary>
public partial class UsuariosGen2Tests
{
    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();

    private static Task AbrirAltaAsync(IRenderedComponent<UsuariosControlados> cut) =>
        cut.Find(".acciones-cabecera button").ClickAsync(new());

    [Fact]
    public async Task Aviso_salir_con_el_alta_a_medias_pregunta()
    {
        Sembrar((Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador));
        var cut = Renderizar();
        await AbrirAltaAsync(cut);

        await EscribirAsync(cut, "Nombre completo", "Nueva Persona");

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    [Fact]
    public async Task Aviso_abrir_el_alta_sin_tocar_nada_no_pregunta()
    {
        Sembrar((Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador));
        var cut = Renderizar();
        await AbrirAltaAsync(cut);

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "el alta abierta y vacía no tiene nada que perder");
    }

    [Fact]
    public async Task Aviso_la_ficha_cargada_al_editar_no_es_un_cambio_y_tocarla_si()
    {
        var jon = Cuenta(JonId, "j.etxebarria@talveg.es", "Jon Etxebarria");
        var ander = Cuenta(AnderId, "a.beitia@talveg.es", "Ander Beitia");
        ander.CoordinadorUsuarioId = JonId;
        Sembrar(
            (Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador),
            (jon, RolesIdentidad.CoordinadorCae),
            (ander, RolesIdentidad.GestorCae));
        _fuente.EnRol = rol => rol == RolesIdentidad.CoordinadorCae ? [jon] : [];

        var cut = Renderizar();
        await PulsarEnMenuAsync(cut, "a.beitia@talveg.es", "Editar");
        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel").Should().NotBeEmpty());
        cut.FindComponents<CampoTexto>().Should().Contain(c => c.Instance.Valor == "Ander Beitia",
            "el test necesita que la ficha llegue cargada");

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "la ficha tal como se cargó no es un cambio de quien edita");

        Navegacion.NavigateTo("/usuarios");
        await PulsarEnMenuAsync(cut, "a.beitia@talveg.es", "Editar");
        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel").Should().NotBeEmpty());
        await EscribirAsync(cut, "Nombre completo", "Ander Beitia Zabala");

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    [Fact]
    public async Task Aviso_guardar_el_alta_no_deja_nada_que_perder()
    {
        Sembrar((Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador));
        var cut = Renderizar();
        await AbrirAltaAsync(cut);
        await EscribirAsync(cut, "Correo", "nuevo@talveg.es");
        await EscribirAsync(cut, "Nombre completo", "Nueva Persona");

        await GuardarAsync(cut);
        cut.WaitForAssertion(() => cut.FindAll(".enlace-activacion").Should().NotBeEmpty());

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "lo escrito ya está guardado");
    }

    [Fact]
    public async Task Aviso_desactivar_con_un_Gestor_CAE_de_destino_elegido_pregunta_y_sin_elegir_no()
    {
        var iker = Cuenta(IkerId, "i.larra@talveg.es", "Iker Larrañaga");
        var ander = Cuenta(AnderId, "a.beitia@talveg.es", "Ander Beitia");
        Sembrar(
            (Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador),
            (ander, RolesIdentidad.GestorCae),
            (iker, RolesIdentidad.GestorCae));
        _fuente.Carteras = _ => new Dictionary<Guid, CarteraDeUsuario> { [AnderId] = new(false, [ClienteUno]) };
        _fuente.EnRol = rol => rol == RolesIdentidad.GestorCae ? [ander, iker] : [];

        var cut = Renderizar(actorId: MartaId);
        await PulsarEnMenuAsync(cut, "a.beitia@talveg.es", "Desactivar");
        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "sin destino elegido no hay nada que perder");

        Navegacion.NavigateTo("/usuarios");
        await PulsarEnMenuAsync(cut, "a.beitia@talveg.es", "Desactivar");
        await cut.Find("[role=dialog] select").ChangeAsync(new ChangeEventArgs { Value = IkerId.ToString() });

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }
}
