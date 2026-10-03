using Bunit;
using CaeManager.Application.Empresas.Queries.BuscarEmpresaPorCif;
using CaeManager.Infrastructure.Identity;
using FluentAssertions;
using RolesIdentidad = CaeManager.Infrastructure.Identity.Roles;

namespace CaeManager.Web.Tests;

/// <summary>
/// S12 (lote 2b), piloto Usuarios: el alta y la edición de usuario usan <c>DrawerFormulario</c>. «Guardar» seguía deshabilitado sin
/// decir por qué mientras viajaba la comprobación del CIF de la empresa a vincular (D-02, D-06: el primario mudo): ahora el kit
/// exige el motivo y sale como title del botón. «Cancelar» además se deshabilita mientras se guarda (antes no).
/// </summary>
public partial class UsuariosGen2Tests
{
    [Fact]
    public async Task Guardar_deshabilitado_mientras_se_comprueba_el_CIF_dice_por_que_y_se_habilita_al_llegar()
    {
        Sembrar((Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador));
        var respuesta = new TaskCompletionSource<object?>();
        _mediador.Retener = peticion => peticion is BuscarEmpresaPorCifQuery ? respuesta.Task : null;

        var cut = Renderizar();
        await cut.Find(".acciones-cabecera button").ClickAsync(new());
        await CampoPorEtiqueta(cut, "Rol").ChangeAsync(new() { Value = RolesIdentidad.Cliente });
        cut.Find(".drawer-pie .boton-primario").HasAttribute("disabled").Should().BeFalse("sin búsqueda en vuelo se puede guardar");

        // La consulta queda retenida: no se espera la escritura entera.
        var escritura = EscribirAsync(cut, "Identificación fiscal de la empresa a vincular", "A48220917");

        cut.WaitForAssertion(() =>
        {
            var guardar = cut.Find(".drawer-pie .boton-primario");
            guardar.HasAttribute("disabled").Should().BeTrue("guardar con la comprobación en vuelo decidiría con un resultado que no ha llegado");
            guardar.GetAttribute("title").Should().Be("Comprobando la identificación fiscal de la empresa…",
                "un «Guardar» deshabilitado sin motivo no existe (D-02, D-06)");
        });

        respuesta.SetResult(new EmpresaPorCifDto(EmpresaId, "Refrielectric S.A.", "A48220917"));
        await escritura;

        cut.WaitForAssertion(() =>
        {
            var guardar = cut.Find(".drawer-pie .boton-primario");
            guardar.HasAttribute("disabled").Should().BeFalse();
            guardar.HasAttribute("title").Should().BeFalse("habilitado no lleva motivo");
        });
    }
}
