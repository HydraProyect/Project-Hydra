using FluentAssertions;
using Xunit;

namespace CaeManager.IntegrationTests.Coherencia;

/// <summary>
/// D-7 plataforma (decisión del propietario, 2026-10-04): una vigencia vencida en la plataforma del Cliente empresarial y una
/// acreditación Rechazada ya no marcan el Centro «Bloqueado»: bloquean al Trabajador de la acreditación o, si es de Empresa, a
/// los Trabajadores de esa Empresa con Asignación activa en ese Centro. «Bloqueado» es un estado del Trabajador, nunca del Centro.
///
/// <para>
/// Es el oráculo del escenario <see cref="EscenarioDeFotoDePlataforma"/>: cada línea esperada está deducida a mano de la regla
/// (comentada junto a ella), no copiada de una ejecución. La misma foto se tomó sobre <c>origin/main</c> antes del cambio; la
/// comparación y la explicación de cada diferencia van en el cuerpo de la PR. Cada superficie lee el MISMO evaluador, así que
/// la coherencia entre ellas es parte de lo que se afirma aquí: Mi trabajo, Centro 360, Inicio, Visión de cartera, la ficha del
/// Cliente empresarial, la lista de Centros y la cola de la Bandeja.
/// </para>
/// </summary>
public class LaPlataformaBloqueaAlTrabajadorYNoAlCentroTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private EscenarioDeFotoDePlataforma _escenario = null!;
    private SortedDictionary<string, List<string>> _foto = null!;

    public async Task InitializeAsync()
    {
        _escenario = new EscenarioDeFotoDePlataforma(_cadenaConexion);
        await _escenario.SembrarAsync();
        _foto = await _escenario.TomarFotoAsync();
    }

    public async Task DisposeAsync()
    {
        await _escenario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    private List<string> Seccion(string nombre) => _foto[nombre];

    [Fact]
    public void Ningun_Centro_sale_Bloqueado_ni_en_la_lista_ni_en_la_ficha_del_Cliente_empresarial()
    {
        Seccion("Centro: estado y recuentos").Should().NotContain(l => l.Contains("estado=Bloqueado"));
        Seccion("Cliente empresarial: ficha").Should().OnlyContain(l => l.Contains("centrosBloqueados=0"));
        Seccion("Inicio").Should().ContainSingle().Which.Should().Contain("centrosBloqueados=0");
    }

    /// <summary>
    /// Cada línea es un Trabajador bloqueado EN ese Centro (Centro 360 y su panel). Deducción:
    /// P1 (Cliente Z, plataforma): la rechazada de Trabajador y la vencida, cada una a su Trabajador; la rechazada con una Asignación
    /// de baja, la de un tipo no aplicable, la que vence hoy, la aceptada sin confirmar, la pendiente y la subida no bloquean; la
    /// Empresa E2 rechazada bloquea a sus DOS Trabajadores en P1 y no al que solo está en P2.
    /// P3: el certificado vencido de E3 no bloquea a nadie, porque E3 no tiene Trabajadores en P3.
    /// P4 (PSS bloqueante documental): una fila por causa; el que cumple pero está rechazado, bloqueado solo por la plataforma.
    /// P5 y P6: el veredicto de la plataforma, en solitario, bloquea al Trabajador y no a su colega.
    /// </summary>
    [Fact]
    public void Cada_Trabajador_bloqueado_aparece_en_el_Centro_donde_lo_bloquea_y_en_ningun_otro()
    {
        Seccion("Centro 360: Trabajadores bloqueados").Should().Equal(
            "Centro P1 | e2 dos en P1 | Certificado SS | Empresa | RechazadoPorPlataforma",
            "Centro P1 | e2 uno en P1 | Certificado SS | Empresa | RechazadoPorPlataforma",
            "Centro P1 | w rechazada en P1 y trabaja tambien en P2 | PSS firmado | Trabajador | RechazadoPorPlataforma",
            "Centro P1 | w vencida en plataforma | PSS firmado | Trabajador | VencidoEnPlataforma",
            "Centro P4 | w4 PSS vencido y rechazado | PSS firmado | Trabajador | RechazadoPorPlataforma",
            "Centro P4 | w4 PSS vencido y rechazado | PSS firmado | Trabajador | Vencido",
            "Centro P4 | w4 cumple el PSS y la plataforma lo rechaza | PSS firmado | Trabajador | RechazadoPorPlataforma",
            "Centro P4 | w4 sin documento | PSS firmado | Trabajador | Ausente",
            "Centro P5 | w5 vigente en TALVEG y vencida en plataforma | PSS firmado | Trabajador | VencidoEnPlataforma",
            "Centro P6 | w6 vigente en TALVEG y rechazada en plataforma | PSS firmado | Trabajador | RechazadoPorPlataforma");
    }

    /// <summary>
    /// Mi trabajo (la fila de requisito pendiente) solo lleva las causas documentales: lo de la plataforma tiene ya su propio item
    /// en la cola (Rechazada/Vencida), y duplicarlo aquí contaría dos veces el mismo bloqueo.
    /// </summary>
    [Fact]
    public void Mi_trabajo_no_duplica_las_causas_de_plataforma_que_ya_tienen_su_item_en_la_cola()
    {
        Seccion("Mi trabajo: requisitos").Should().Equal(
            "Centro P4 | w4 PSS vencido y rechazado | PSS firmado | Trabajador | Vencido",
            "Centro P4 | w4 sin documento | PSS firmado | Trabajador | Ausente");
    }

    /// <summary>
    /// El color del Centro lo dan los documentos y la vigencia en la plataforma; un rechazo no lo cambia.
    /// P1: Faltante (los Trabajadores de E2 no tienen PSS) y la vigencia vencida de la plataforma suma una causa Vencido.
    /// P5: Vencido solo por la plataforma. P6: Vigente, aunque su Trabajador está rechazado y bloqueado.
    /// </summary>
    [Fact]
    public void El_Centro_conserva_el_color_de_sus_documentos_y_un_rechazo_no_lo_cambia()
    {
        var lista = Seccion("Centro: estado y recuentos");
        lista.Should().Contain(l => l.StartsWith("Centro P5 | estado=Vencido"));
        lista.Should().Contain(l => l.StartsWith("Centro P6 | estado=Vigente"));
        lista.Should().Contain(l => l.StartsWith("Centro P1 | estado=Faltante"));
        lista.Single(l => l.StartsWith("Centro P6")).Should().Contain("vencidas=0").And.Contain("proximas=0",
            "el rechazo no es un vencimiento y no inventa una incidencia en el Centro");

        var causas = Seccion("Centro: causas");
        causas.Should().NotContain(l => l.StartsWith("Centro P6"), "el rechazo de la plataforma no es una causa del Centro");
        causas.Should().Contain("Centro P5 | PSS firmado — vencido en la plataforma | estado=Vencido | Trabajador");
        causas.Should().Contain("Centro P3 | Certificado SS — vencido en la plataforma | estado=Vencido | Empresa",
            "la vigencia vencida en la plataforma sigue siendo una causa Vencido del Centro, aunque no bloquee a nadie sin Trabajadores");
    }

    /// <summary>
    /// Trabajadores distintos bloqueados: P1 cuatro con E2 (e2 uno, e2 dos, rechazada, vencida), P4 tres (vencido y rechazado, cumple
    /// y rechazado, sin documento), P5 uno y P6 uno = 9. Cliente Z tiene P1, P2 y P4 (7); Cliente W, P3, P5 y P6 (2).
    /// </summary>
    [Fact]
    public void Inicio_Vision_de_cartera_y_ficha_del_Cliente_empresarial_cuentan_los_mismos_Trabajadores()
    {
        Seccion("Inicio").Should().ContainSingle().Which.Should().Contain("trabajadoresBloqueados=9");
        Seccion("Vision de cartera").Should().ContainSingle().Which.Should()
            .Contain("trabajadoresBloqueados=9").And.Contain("tieneBloqueos=True").And.Contain("admiteVeredictoVerde=False")
            .And.Contain("trabajadoresBloqueadosGlobal=9");
        Seccion("Cliente empresarial: ficha").Should().Equal(
            "Cliente W | trabajadoresBloqueados=2 | centrosBloqueados=0 | centrosConVencidos=2",
            "Cliente Z | trabajadoresBloqueados=7 | centrosBloqueados=0 | centrosConVencidos=3");
    }

    /// <summary>
    /// La cola de la Bandeja marca «bloquea acceso» exactamente en las acreditaciones que el evaluador cuenta: las dos rechazadas
    /// de P4, la de Empresa y la de Trabajador de P1, y la de P6. Las dos de P1 que no bloquean (Asignación de baja, tipo no
    /// aplicable) siguen siendo trabajo pero sin banda.
    /// </summary>
    [Fact]
    public void La_cola_marca_bloquea_acceso_solo_donde_el_evaluador_bloquea()
    {
        Seccion("Cola: items de plataforma y requisitos").Should().Equal(
            "Centro P1 | PlataformaRechazada | bloqueaElAcceso=False | Opcional",
            "Centro P1 | PlataformaRechazada | bloqueaElAcceso=False | PSS firmado",
            "Centro P1 | PlataformaRechazada | bloqueaElAcceso=True | Certificado SS",
            "Centro P1 | PlataformaRechazada | bloqueaElAcceso=True | PSS firmado",
            "Centro P4 | PlataformaRechazada | bloqueaElAcceso=True | PSS firmado",
            "Centro P4 | PlataformaRechazada | bloqueaElAcceso=True | PSS firmado",
            "Centro P4 | RequisitoPendiente | bloqueaElAcceso=True | PSS firmado — Caso w4 PSS vencido y rechazado",
            "Centro P4 | RequisitoPendiente | bloqueaElAcceso=True | PSS firmado — Caso w4 sin documento",
            "Centro P6 | PlataformaRechazada | bloqueaElAcceso=True | PSS firmado");
    }
}
