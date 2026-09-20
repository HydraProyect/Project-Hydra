using CaeManager.Application.AsistenteIa.Ordenes;
using CaeManager.Application.Common;
using FluentAssertions;

namespace CaeManager.Application.Tests.AsistenteIa;

/// <summary>
/// El catálogo es un contrato, no una lista de buenas intenciones. Esto fija sus
/// invariantes para que romperlas salga en rojo en vez de salir en producción.
/// </summary>
public class CatalogoOrdenesAsistenteTests
{
    [Fact]
    public void Ningun_identificador_se_repite_y_ninguno_esta_vacio()
    {
        var ids = CatalogoOrdenesAsistente.Ordenes.Select(o => o.Id).ToList();

        ids.Should().OnlyHaveUniqueItems();
        ids.Should().OnlyContain(id => !string.IsNullOrWhiteSpace(id));
    }

    [Fact]
    public void La_abstencion_no_es_una_orden_del_catalogo()
    {
        // Si la abstención colisionara con un identificador de orden, el asistente
        // no podría distinguir «no pide nada» de «pide esto».
        CatalogoOrdenesAsistente.Ordenes.Should()
            .NotContain(o => o.Id == CatalogoOrdenesAsistente.Abstencion);
        CatalogoOrdenesAsistente.CriterioAbstencion.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Toda_orden_declara_criterio_y_al_menos_un_campo_obligatorio()
    {
        // Una orden sin ningún dato obligatorio no es una orden: no hay nada que
        // ejecutar ni nada que preguntar cuando falte.
        foreach (var orden in CatalogoOrdenesAsistente.Ordenes)
        {
            orden.Criterio.Should().NotBeNullOrWhiteSpace(
                "la orden {0} se clasifica con su criterio", orden.Id);
            orden.CamposObligatorios.Should().NotBeEmpty(
                "la orden {0} necesita algún dato para poder ejecutarse", orden.Id);
        }
    }

    [Fact]
    public void Ningun_campo_se_repite_dentro_de_la_misma_orden()
    {
        foreach (var orden in CatalogoOrdenesAsistente.Ordenes)
        {
            orden.Campos.Select(c => c.Nombre).Should().OnlyHaveUniqueItems(
                "los campos de {0} se identifican por su nombre", orden.Id);
        }
    }

    [Fact]
    public void Una_orden_ejecutable_tiene_con_que_ejecutarse()
    {
        // Es la invariante que impide que el asistente prometa algo que nadie
        // sabe hacer: o no escribe nada, o declara los Commands que lo hacen.
        foreach (var orden in CatalogoOrdenesAsistente.Ordenes.Where(o => o.Ejecutable))
        {
            (orden.EsSoloLectura || orden.Commands.Count > 0).Should().BeTrue(
                "la orden {0} se declara ejecutable, así que necesita Commands o ser de solo lectura",
                orden.Id);
        }
    }

    [Fact]
    public void Una_orden_no_ejecutable_dice_por_que()
    {
        foreach (var orden in CatalogoOrdenesAsistente.Ordenes.Where(o => !o.Ejecutable))
        {
            orden.Limitacion.Should().NotBeNullOrWhiteSpace(
                "si {0} no se puede ejecutar, el catálogo tiene que decir qué falta", orden.Id);
        }
    }

    [Fact]
    public void Una_orden_de_solo_lectura_no_declara_Commands()
    {
        foreach (var orden in CatalogoOrdenesAsistente.Ordenes.Where(o => o.EsSoloLectura))
        {
            orden.Commands.Should().BeEmpty(
                "{0} es de solo lectura: si despacha un Command, escribe", orden.Id);
        }
    }

    [Fact]
    public void Todo_Command_declarado_es_un_Command_de_verdad()
    {
        // Sin esto, cualquier tipo podría colarse en la lista y el catálogo
        // mentiría sobre lo que ejecuta.
        foreach (var orden in CatalogoOrdenesAsistente.Ordenes)
        {
            foreach (var tipo in orden.Commands)
            {
                typeof(ICommandBase).IsAssignableFrom(tipo).Should().BeTrue(
                    "{0} declara {1}, que tendría que implementar ICommand", orden.Id, tipo.Name);
            }
        }
    }

    [Fact]
    public void Toda_frontera_apunta_a_una_orden_que_existe_y_es_reciproca()
    {
        var porId = CatalogoOrdenesAsistente.Ordenes.ToDictionary(o => o.Id);

        foreach (var orden in CatalogoOrdenesAsistente.Ordenes)
        {
            foreach (var frontera in orden.Fronteras)
            {
                porId.Should().ContainKey(frontera.ConLaOrden,
                    "{0} declara una frontera con una orden inexistente", orden.Id);
                frontera.Regla.Should().NotBeNullOrWhiteSpace(
                    "la frontera entre {0} y {1} tiene que decir qué las separa",
                    orden.Id, frontera.ConLaOrden);

                // Una frontera que solo se declara por un lado deja el otro lado
                // clasificando sin regla, que es justo el caso medido donde el
                // modelo elige por su cuenta.
                porId[frontera.ConLaOrden].Fronteras.Should()
                    .Contain(f => f.ConLaOrden == orden.Id,
                        "{0} declara frontera con {1}, así que {1} debe declararla con {0}",
                        orden.Id, frontera.ConLaOrden);
            }
        }
    }

    [Fact]
    public void Dos_ordenes_que_comparten_datos_obligatorios_declaran_su_frontera()
    {
        // Si dos órdenes piden lo mismo, distinguirlas no puede quedar al criterio
        // del modelo: medido, elige una con probabilidad 0,86 sin que se lo pidan.
        var ordenes = CatalogoOrdenesAsistente.Ordenes;

        for (var i = 0; i < ordenes.Count; i++)
        {
            for (var j = i + 1; j < ordenes.Count; j++)
            {
                var comunes = ordenes[i].CamposObligatorios.Select(c => c.Nombre)
                    .Intersect(ordenes[j].CamposObligatorios.Select(c => c.Nombre))
                    .ToList();

                if (comunes.Count <= 1)
                {
                    continue;
                }

                ordenes[i].Fronteras.Should().Contain(f => f.ConLaOrden == ordenes[j].Id,
                    "{0} y {1} comparten los datos obligatorios {2}, así que hace falta decir qué las separa",
                    ordenes[i].Id, ordenes[j].Id, string.Join(", ", comunes));
            }
        }
    }

    [Fact]
    public void Una_frontera_sin_confirmar_se_declara_como_limitacion()
    {
        // Una regla que propuso quien escribió el catálogo no es una decisión de
        // negocio. Mientras no lo sea, la orden tiene que llevarlo escrito.
        foreach (var orden in CatalogoOrdenesAsistente.Ordenes.Where(o => o.TieneFronteraSinConfirmar))
        {
            orden.Limitacion.Should().NotBeNullOrWhiteSpace(
                "{0} tiene una frontera sin confirmar por negocio y no lo dice", orden.Id);
        }
    }

    [Fact]
    public void Lo_que_sale_fuera_de_TALVEG_se_declara_y_se_confirma()
    {
        foreach (var orden in CatalogoOrdenesAsistente.Ordenes.Where(o => o.EnviaComunicacionExterna))
        {
            orden.RequiereConfirmacion.Should().BeTrue(
                "{0} manda algo a un tercero", orden.Id);
            orden.Limitacion.Should().NotBeNullOrWhiteSpace(
                "{0} no se deshace borrando un registro, y eso tiene que constar", orden.Id);
        }
    }

    [Fact]
    public void Todas_las_ordenes_requieren_confirmacion_humana_en_esta_version()
    {
        // Cuando alguna deje de requerirla, que sea una línea visible en un diff
        // y no un descuido.
        CatalogoOrdenesAsistente.Ordenes.Should().OnlyContain(o => o.RequiereConfirmacion);
    }

    [Fact]
    public void Se_encuentra_una_orden_por_su_identificador()
    {
        CatalogoOrdenesAsistente.PorId(CatalogoOrdenesAsistente.AltaCentro)
            .Should().NotBeNull();
        CatalogoOrdenesAsistente.PorId("no_existe").Should().BeNull();
    }
}
