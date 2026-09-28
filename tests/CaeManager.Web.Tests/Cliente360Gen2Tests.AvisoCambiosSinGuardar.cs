using Bunit;
using CaeManager.Application.Clientes.Commands.EditarCliente;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// P1-E2b (lote 1): el panel del Cliente empresarial pinta su propio aviso de cambios sin
/// guardar para la edición en línea de la identidad y para la nota interna, que está siempre
/// abierta y cuenta como cambio en cuanto difiere de la guardada.
/// </summary>
public partial class Cliente360Gen2Tests
{
    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();

    private (Guid Id, MediatorFalso Mediador) FichaConNota()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Refrielectric S.A.", notas: "Carmen prefiere antes de las 10:00.");
        mediador.Resumenes[id] = Resumen(id, 3, 42);
        return (id, mediador);
    }

    [Fact]
    public async Task Aviso_la_identidad_editada_pregunta_al_salir()
    {
        var (id, _) = FichaConNota();
        var cut = Renderizar(id);
        await Boton(cut, "Editar identidad").ClickAsync(new MouseEventArgs());
        await EscribirAsync(cut.FindAll("input[type=text]")[0], "Refrielectric Sociedad Anónima");

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    [Fact]
    public async Task Aviso_abrir_la_identidad_sin_tocar_nada_no_pregunta()
    {
        var (id, _) = FichaConNota();
        var cut = Renderizar(id);
        await Boton(cut, "Editar identidad").ClickAsync(new MouseEventArgs());

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "los valores de partida no son un cambio de quien edita");
    }

    [Fact]
    public async Task Aviso_la_nota_escrita_y_sin_guardar_pregunta_al_salir()
    {
        var (id, _) = FichaConNota();
        var cut = Renderizar(id, "notas");
        await EscribirAsync(cut.Find("textarea"), "El acceso al Centro Norte exige formación de altura.");

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    [Fact]
    public async Task Aviso_la_nota_guardada_deja_de_contar_como_cambio()
    {
        var (id, mediador) = FichaConNota();
        var cut = Renderizar(id, "notas");
        const string nota = "El acceso al Centro Norte exige formación de altura.";
        await EscribirAsync(cut.Find("textarea"), nota);
        // El servidor devuelve ya la nota guardada al recargar la cabecera.
        mediador.Detalles[id] = mediador.Detalles[id] with { Notas = nota };

        await Boton(cut, "Guardar nota").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EditarClienteCommand>().Should().ContainSingle();
        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "la nota guardada es el nuevo punto de partida");
    }
}
