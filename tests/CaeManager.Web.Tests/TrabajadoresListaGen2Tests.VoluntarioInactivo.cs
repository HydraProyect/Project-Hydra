using Bunit;
using CaeManager.Application.Trabajadores.Commands.CrearTrabajador;
using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// Tipo de contratación y voluntario inactivo en el listado de Trabajadores: el indicador se pinta
/// solo cuando la consulta lo marca, no hay pastilla «Actividad» (Chris, 2026-10-18) y el alta envía el
/// tipo elegido en el selector.
/// </summary>
public partial class TrabajadoresListaGen2Tests
{
    [Fact]
    public void El_voluntario_inactivo_lleva_indicador_y_el_resto_no()
    {
        var inactivo = Trabajador("Lucía", "Pardo");
        inactivo = inactivo with { Dto = inactivo.Dto with { TipoContratacion = TipoContratacion.Voluntario, VoluntarioInactivo = true } };
        var activo = Trabajador("Bea", "Alonso");
        var mediador = new MediatorFalso { Almacen = { inactivo, activo } };

        var cut = Renderizar(mediador);

        var conIndicador = FilasDeDatos(cut).Where(tr => tr.QuerySelector(".voluntario-inactivo") is not null).ToList();
        conIndicador.Should().ContainSingle("solo el voluntario sin evento vigente ni futuro está inactivo");
        conIndicador.Single().TextContent.Should().Contain("Pardo").And.Contain("Voluntario inactivo");
    }

    /// <summary>
    /// Decisión de Chris (2026-10-18): la pastilla «Actividad» se retira. La lista sigue mostrando a todos (el voluntario inactivo
    /// conserva su indicador en la fila); la consulta ya no tiene parámetro de actividad.
    /// </summary>
    [Fact]
    public void La_lista_no_ofrece_la_pastilla_Actividad()
    {
        var mediador = new MediatorFalso { Almacen = { Trabajador("Bea", "Alonso") } };
        var cut = Renderizar(mediador);

        cut.FindComponents<PastillaFiltro>().Select(p => p.Instance.Etiqueta).Should().NotContain("Actividad");
    }

    [Fact]
    public async Task El_alta_envia_el_tipo_de_contratacion_elegido()
    {
        var nueva = Trabajador("Carla", "Molina Ríos");
        var mediador = new MediatorFalso { Almacen = { Trabajador("Bea", "Alonso") }, EmpleadoresFiltro = CatalogoSoloEbroParaRefresh() };
        mediador.Retener = p => p is CrearTrabajadorCommand
            ? Task.FromResult<object>(Result.Exito(nueva.Dto.Id)) : null;
        var cut = Renderizar(mediador);
        await AbrirAltaAsync(cut);
        await SelectorDeEmpleadorDelAlta(cut).Find("select").ChangeAsync(new ChangeEventArgs { Value = EmpresaEbro.ToString() });
        await EscribirDatosCompletosDelAltaAsync(cut);
        var selector = cut.FindComponents<CampoSelect>().Single(c => c.Instance.Etiqueta == "Tipo de contratación");
        selector.Instance.Valor.Should().Be("CuentaAjena", "el alta propone cuenta ajena por defecto");

        await selector.Find("select").ChangeAsync(new ChangeEventArgs { Value = "Voluntario" });
        await GuardarAlta(cut).ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<CrearTrabajadorCommand>().Should().ContainSingle()
            .Which.TipoContratacion.Should().Be(TipoContratacion.Voluntario);
    }
}
