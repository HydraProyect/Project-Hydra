using Bunit;
using CaeManager.Application.Tenants.Commands.CrearClienteDelegante;
using CaeManager.Application.Tenants.Commands.CrearOperadorCaeExterno;
using CaeManager.Application.Tenants.Queries.AutorizarOperadorCaeExterno;
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
}
