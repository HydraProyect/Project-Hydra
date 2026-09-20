using CaeManager.Application.AsistenteIa.Ordenes;
using FluentAssertions;
using MediatR;

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
        // sabe hacer. Declararse de solo lectura ya no basta para librarse: una
        // consulta sin consulta declarada es igual de inejecutable que un alta
        // sin Command.
        foreach (var orden in CatalogoOrdenesAsistente.Ordenes.Where(o => o.Ejecutable))
        {
            orden.Ejecucion.Should().NotBeEmpty(
                "la orden {0} se declara ejecutable, así que tiene que decir con qué", orden.Id);
        }
    }

    [Fact]
    public void Cuando_los_pasos_son_alternativas_cada_uno_dice_cuando_se_elige()
    {
        // Sin condición, quien despacha no puede saber cuál toca, y con dos
        // alternativas de reclamación eso significa mandar dos correos.
        foreach (var orden in CatalogoOrdenesAsistente.Ordenes
                     .Where(o => o.Modo == ModoDeEjecucion.Alternativa))
        {
            orden.Ejecucion.Should().HaveCountGreaterThan(1,
                "{0} se declara alternativa pero no ofrece entre qué elegir", orden.Id);
            orden.Ejecucion.Should().OnlyContain(p => !string.IsNullOrWhiteSpace(p.Cuando),
                "cada alternativa de {0} tiene que decir cuándo se elige", orden.Id);
        }
    }

    [Fact]
    public void Una_secuencia_no_declara_condiciones_por_paso()
    {
        // En una secuencia se ejecutan todos: una condición ahí sería una
        // alternativa mal declarada.
        foreach (var orden in CatalogoOrdenesAsistente.Ordenes
                     .Where(o => o.Modo == ModoDeEjecucion.Secuencia))
        {
            orden.Ejecucion.Should().OnlyContain(p => string.IsNullOrEmpty(p.Cuando),
                "{0} es una secuencia: todos sus pasos se ejecutan", orden.Id);
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
    public void Toda_operacion_declarada_es_despachable()
    {
        // Sin esto, cualquier tipo podría colarse y el catálogo mentiría sobre lo
        // que ejecuta. Un Command implementa ICommandBase; una Query, IRequest.
        foreach (var orden in CatalogoOrdenesAsistente.Ordenes)
        {
            foreach (var paso in orden.Ejecucion)
            {
                var esDespachable = paso.Operacion.GetInterfaces()
                    .Any(i => i == typeof(IBaseRequest) ||
                              (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>)));

                esDespachable.Should().BeTrue(
                    "{0} declara {1}, que tendría que ser un Command o una Query",
                    orden.Id, paso.Operacion.Name);
            }
        }
    }

    [Fact]
    public void Una_orden_que_escribe_no_puede_pasar_por_solo_lectura()
    {
        // EsSoloLectura se deriva de los pasos, así que esto comprueba que la
        // derivación funciona y no que alguien la declaró bien.
        var reclamar = CatalogoOrdenesAsistente.PorId(CatalogoOrdenesAsistente.ReclamarDocumentacion)!;
        var consulta = CatalogoOrdenesAsistente.PorId(CatalogoOrdenesAsistente.ConsultaDeEstado)!;

        reclamar.EsSoloLectura.Should().BeFalse();
        consulta.EsSoloLectura.Should().BeTrue();
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
    public void Todas_las_ordenes_requieren_confirmacion_humana()
    {
        // Decisión adoptada por el propietario el 2026-09-20: un Enter sobre el
        // plan, sin ejecución directa. No es una propiedad de esta versión: es la
        // regla. Cuando alguna orden deje de requerirla, que sea una línea visible
        // en un diff y no un descuido.
        CatalogoOrdenesAsistente.Ordenes.Should().OnlyContain(o => o.RequiereConfirmacion);
    }

    [Fact]
    public void Se_encuentra_una_orden_por_su_identificador()
    {
        CatalogoOrdenesAsistente.PorId(CatalogoOrdenesAsistente.AltaCentro)
            .Should().NotBeNull();
        CatalogoOrdenesAsistente.PorId("no_existe").Should().BeNull();
    }

    [Fact]
    public void La_frontera_entre_asignacion_y_visita_esta_confirmada_y_es_la_misma_de_los_dos_lados()
    {
        // La regla la dio el propietario el 2026-09-21. Que esté confirmada no es
        // burocracia: mientras fue una propuesta, el catálogo tenía que llevarlo
        // escrito en la limitación, y el asistente presentaba las dos órdenes
        // como alternativas. No lo son.
        var asignacion = CatalogoOrdenesAsistente.PorId(CatalogoOrdenesAsistente.AltaTrabajadorYAsignacion)!;
        var visita = CatalogoOrdenesAsistente.PorId(CatalogoOrdenesAsistente.VisitaPuntualACentro)!;

        var desdeAsignacion = asignacion.Fronteras.Single(f => f.ConLaOrden == visita.Id);
        var desdeVisita = visita.Fronteras.Single(f => f.ConLaOrden == asignacion.Id);

        desdeAsignacion.ConfirmadaPorNegocio.Should().BeTrue();
        desdeVisita.ConfirmadaPorNegocio.Should().BeTrue();

        // Literalmente la misma regla, no dos redacciones parecidas: dos textos
        // que dicen lo mismo hoy acaban diciendo cosas distintas cuando alguien
        // toca uno.
        desdeVisita.Regla.Should().Be(desdeAsignacion.Regla);
        desdeAsignacion.Regla.Should().Be(CatalogoOrdenesAsistente.ReglaFronteraAsignacionVisita);
    }

    [Fact]
    public void La_regla_de_la_frontera_dice_que_no_son_alternativas()
    {
        // El error que corrige es concreto y tiene consecuencia: presentarlas
        // como excluyentes llevaba a tramitar la entrada de alguien que todavía
        // no puede entrar porque no está dado de alta.
        CatalogoOrdenesAsistente.ReglaFronteraAsignacionVisita
            .Should().Contain("NO son alternativas");
    }

    [Fact]
    public void El_ingreso_a_un_centro_cubre_las_tres_situaciones_de_canal()
    {
        var visita = CatalogoOrdenesAsistente.PorId(CatalogoOrdenesAsistente.VisitaPuntualACentro)!;

        // Si falta una situación, hay órdenes reales que caen por un agujero sin
        // que nadie lo note: el asistente propondría el camino de otra.
        visita.Caminos.Select(c => c.Canal).Should().Contain(
        [
            SituacionDelCanal.Plataforma,
            SituacionDelCanal.Correo,
            SituacionDelCanal.SinAveriguar,
        ]);

        visita.Caminos.Select(c => c.Id).Should().OnlyHaveUniqueItems();
        visita.Caminos.Should().HaveCount(5,
            "plataforma se parte en dos según haya alta o no, y «no se sabe el canal» en dos " +
            "según el Cliente empresarial ya sea nuestro o no");
    }

    [Fact]
    public void Hoy_ningun_camino_de_ingreso_se_completa_sin_el_gestor()
    {
        // No es una propiedad deseable: es el estado de las cosas, y está aquí
        // para que deje de serlo con una línea visible en un diff en vez de con
        // un descuido. Los cinco caminos acaban en una acción del Gestor CAE
        // —subir a la plataforma, pedir un alta, enviar un correo—, y ninguna de
        // las tres la sabe hacer TALVEG sola.
        //
        // El primero que se completará será el del correo, en cuanto exista un
        // flujo que cree la conversación de origen. Cuando llegue ese día, esta
        // prueba se cambia a propósito, no se descubre rota: era ejecutable
        // ANTES de que existiera el flujo y eso habría dejado confirmar Visitas
        // que no enviaban nada (hallazgo de Codex, 2026-09-21).
        var visita = CatalogoOrdenesAsistente.PorId(CatalogoOrdenesAsistente.VisitaPuntualACentro)!;

        visita.Caminos.Should().OnlyContain(c => !c.Ejecutable);
        visita.TieneCaminoNoEjecutable.Should().BeTrue();
    }

    [Fact]
    public void Toda_macro_declarada_la_propone_algun_camino()
    {
        // Esta prueba nace de un hallazgo de Codex (2026-09-21). Las dos macros
        // estaban declaradas, pero una sola rama cubría «no se sabe el canal» y
        // proponía siempre la de presentación; la otra vivía únicamente dentro
        // del texto de la limitación, en prosa, donde ningún consumidor del
        // catálogo puede leerla. Resultado: se habría propuesto el correo de
        // presentación a un Cliente empresarial que lleva años con nosotros.
        //
        // Mira en la dirección contraria a la prueba de al lado a propósito: allí
        // se comprueba que ninguna rama propone una macro inexistente; aquí, que
        // ninguna macro existente se queda sin proponer. Una sola de las dos deja
        // medio contrato sin vigilar.
        var propuestas = CatalogoOrdenesAsistente.Ordenes
            .SelectMany(o => o.Caminos)
            .Select(c => c.MacroSugerida)
            .Where(m => !string.IsNullOrEmpty(m))
            .ToHashSet();

        propuestas.Should().Contain(MacrosDeMuestraAsistente.PresentacionCentroDesconocido);
        propuestas.Should().Contain(MacrosDeMuestraAsistente.SolicitudAltaDeCentro);
    }

    [Fact]
    public void Dos_caminos_que_proponen_macros_distintas_no_aplican_en_lo_mismo()
    {
        // Si dos ramas proponen plantillas distintas, su condición tiene que
        // distinguirlas: con la misma condición, quien consuma el catálogo no
        // puede elegir y acabará cogiendo la primera de la lista.
        foreach (var orden in CatalogoOrdenesAsistente.Ordenes)
        {
            var conMacro = orden.Caminos.Where(c => !string.IsNullOrEmpty(c.MacroSugerida)).ToList();

            conMacro.Select(c => c.Cuando).Should().OnlyHaveUniqueItems(
                "en {0} hay ramas con plantillas distintas y la misma condición", orden.Id);
        }
    }

    [Fact]
    public void Una_orden_con_algun_camino_sin_completar_lo_dice_en_su_limitacion()
    {
        // Una orden puede ser ejecutable y aun así dejar el trabajo a medias por
        // el camino que toque. Si no lo dice, quien confirma el plan cree que ya
        // está hecho — que es el modo exacto en que esta orden falla: la Visita
        // queda registrada en TALVEG y sin acreditar donde importa.
        foreach (var orden in CatalogoOrdenesAsistente.Ordenes.Where(o => o.TieneCaminoNoEjecutable))
        {
            orden.Limitacion.Should().NotBeNullOrWhiteSpace(
                "{0} tiene caminos que no se completan y no lo advierte", orden.Id);
        }
    }

    [Fact]
    public void Un_camino_que_no_se_puede_completar_dice_por_que()
    {
        foreach (var orden in CatalogoOrdenesAsistente.Ordenes)
        {
            foreach (var camino in orden.Caminos.Where(c => !c.Ejecutable))
            {
                camino.Limitacion.Should().NotBeNullOrWhiteSpace(
                    "el camino {0} de {1} no se puede completar y no dice por qué", camino.Id, orden.Id);
            }
        }
    }

    [Fact]
    public void Todo_camino_dice_cuando_aplica_y_que_se_hace()
    {
        foreach (var orden in CatalogoOrdenesAsistente.Ordenes)
        {
            foreach (var camino in orden.Caminos)
            {
                camino.Cuando.Should().NotBeNullOrWhiteSpace("{0} no dice cuándo aplica", camino.Id);
                camino.QueSeHace.Should().NotBeNullOrWhiteSpace("{0} no dice qué se hace", camino.Id);
            }
        }
    }

    [Fact]
    public void Un_camino_que_propone_una_macro_propone_una_que_existe()
    {
        var conocidas = new[]
        {
            MacrosDeMuestraAsistente.PresentacionCentroDesconocido,
            MacrosDeMuestraAsistente.SolicitudAltaDeCentro,
        };

        var conMacro = CatalogoOrdenesAsistente.Ordenes
            .SelectMany(o => o.Caminos)
            .Where(c => !string.IsNullOrEmpty(c.MacroSugerida))
            .ToList();

        // Control positivo: sin esta línea, el bucle de abajo recorre una lista
        // vacía y da verde el día que nadie proponga ninguna plantilla — que es
        // justo cuando habría que enterarse.
        conMacro.Should().NotBeEmpty("alguna rama tiene que proponer una plantilla de correo");

        foreach (var camino in conMacro)
        {
            conocidas.Should().Contain(camino.MacroSugerida,
                "el camino {0} propone una plantilla que no está declarada", camino.Id);
        }
    }

    [Fact]
    public void Las_dos_macros_tienen_cuerpo_de_muestra_y_no_se_confunden_entre_si()
    {
        MacrosDeMuestraAsistente.CuerpoDeMuestraPresentacion.Should().NotBeNullOrWhiteSpace();
        MacrosDeMuestraAsistente.CuerpoDeMuestraSolicitudAlta.Should().NotBeNullOrWhiteSpace();
        MacrosDeMuestraAsistente.PresentacionCentroDesconocido
            .Should().NotBe(MacrosDeMuestraAsistente.SolicitudAltaDeCentro);

        // La diferencia que las justifica: a quien ya nos conoce no se le vuelve
        // a preguntar por el canal. Preguntárselo otra vez es la forma más rápida
        // de que el correo se quede sin contestar.
        MacrosDeMuestraAsistente.CuerpoDeMuestraPresentacion.Should().Contain("plataforma de coordinación");
        MacrosDeMuestraAsistente.CuerpoDeMuestraSolicitudAlta.Should().NotContain("¿disponen de una plataforma");
    }

    [Fact]
    public void Un_camino_que_ejecuta_algo_no_se_declara_inejecutable_sin_decirlo()
    {
        // Control del propio catálogo: un camino con pasos declarados y
        // Ejecutable=false es legítimo —el Command existe pero no basta—, pero
        // entonces la limitación tiene que explicar qué falta, porque si no
        // parece una contradicción y se "arregla" poniéndolo a true.
        foreach (var orden in CatalogoOrdenesAsistente.Ordenes)
        {
            foreach (var camino in orden.Caminos.Where(c => c.Ejecucion.Count > 0 && !c.Ejecutable))
            {
                camino.Limitacion.Should().NotBeNullOrWhiteSpace(
                    "{0} declara operaciones y aun así no se puede completar", camino.Id);
            }
        }
    }
}
