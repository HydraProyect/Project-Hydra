using CaeManager.Application.Visitas.PaqueteDocumental;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Visitas;

/// <summary>
/// El orden puro con el que el paquete de acreditación elige qué copia vigente viaja: emisión más
/// reciente primero (decisión del propietario, 2026-10-01) y un desempate determinista y total.
/// Cada paso se aísla variando UNA sola clave, para que ningún paso preste evidencia a otro.
/// </summary>
public class PreferenciaCopiaDelPaqueteTests
{
    private static readonly DateOnly Hoy = new(2026, 10, 3);
    private static readonly DateTime Alta = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    private sealed record Copia(
        string Nombre, DateOnly Emision, EstadoVigenciaDocumento Estado, DateOnly? Vencimiento, DateTime Creado, Guid Id);

    private static Copia Nueva(
        string nombre, int emisionHaceDias, EstadoVigenciaDocumento estado = EstadoVigenciaDocumento.VenceEnFecha,
        int? venceEnDias = 100, int altaMinutos = 0, Guid? id = null) =>
        new(nombre, Hoy.AddDays(-emisionHaceDias), estado,
            estado == EstadoVigenciaDocumento.VenceEnFecha ? Hoy.AddDays(venceEnDias!.Value) : null,
            Alta.AddMinutes(altaMinutos), id ?? Guid.NewGuid());

    private static List<string> Orden(params Copia[] copias) =>
        PreferenciaCopiaDelPaquete.Ordenar(copias, c => c.Emision, c => c.Estado, c => c.Vencimiento, c => c.Creado, c => c.Id)
            .Select(c => c.Nombre).ToList();

    [Fact]
    public void La_emision_mas_reciente_gana_aunque_venza_antes()
    {
        Orden(Nueva("larga", emisionHaceDias: 90, venceEnDias: 900), Nueva("reciente", emisionHaceDias: 2, venceEnDias: 5))
            .Should().Equal("reciente", "larga");
    }

    [Fact]
    public void La_emision_manda_sobre_la_vigencia_no_caduca()
    {
        Orden(
                Nueva("no-caduca-antigua", 900, EstadoVigenciaDocumento.NoCaduca),
                Nueva("con-fecha-reciente", 3, venceEnDias: 30))
            .Should().Equal("con-fecha-reciente", "no-caduca-antigua");
    }

    [Fact]
    public void Sin_confirmar_compite_por_su_emision_como_cualquier_otra_copia()
    {
        Orden(
                Nueva("confirmada-antigua", 200, venceEnDias: 400),
                Nueva("sin-confirmar-reciente", 4, EstadoVigenciaDocumento.SinConfirmar))
            .Should().Equal("sin-confirmar-reciente", "confirmada-antigua");
    }

    [Fact]
    public void A_igual_emision_la_confirmada_precede_a_la_sin_confirmar()
    {
        Orden(
                Nueva("sin-confirmar", 10, EstadoVigenciaDocumento.SinConfirmar),
                Nueva("con-fecha", 10, venceEnDias: 1))
            .Should().Equal("con-fecha", "sin-confirmar");
    }

    [Fact]
    public void A_igual_emision_gana_la_que_vence_mas_tarde_y_no_caduca_es_la_maxima()
    {
        Orden(
                Nueva("vence-pronto", 10, venceEnDias: 20),
                Nueva("no-caduca", 10, EstadoVigenciaDocumento.NoCaduca),
                Nueva("vence-tarde", 10, venceEnDias: 700))
            .Should().Equal("no-caduca", "vence-tarde", "vence-pronto");
    }

    [Fact]
    public void A_igual_emision_y_vencimiento_gana_la_dada_de_alta_mas_tarde()
    {
        Orden(
                Nueva("alta-temprano", 10, altaMinutos: 0),
                Nueva("alta-tarde", 10, altaMinutos: 90),
                Nueva("alta-media", 10, altaMinutos: 30))
            .Should().Equal("alta-tarde", "alta-media", "alta-temprano");
    }

    [Fact]
    public void Con_todo_igual_decide_el_menor_Id_y_el_resultado_no_depende_del_orden_de_entrada()
    {
        var a = Nueva("id-1", 10, id: new Guid("00000000-0000-0000-0000-000000000001"));
        var b = Nueva("id-2", 10, id: new Guid("00000000-0000-0000-0000-000000000002"));
        var c = Nueva("id-3", 10, id: new Guid("00000000-0000-0000-0000-000000000003"));

        Orden(a, b, c).Should().Equal("id-1", "id-2", "id-3");
        Orden(c, b, a).Should().Equal("id-1", "id-2", "id-3");
        Orden(b, c, a).Should().Equal("id-1", "id-2", "id-3");
    }
}
