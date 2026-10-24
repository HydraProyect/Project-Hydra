using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.EstadoMaterializado;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.EstadoMaterializado;

public class EstadoMaterializadoEnumsTests
{
    [Fact]
    public void EstadoTalvegRequisito_conserva_cada_EstadoDocumento_y_anade_NoAplica()
    {
        foreach (var estado in Enum.GetValues<EstadoDocumento>())
        {
            var deRequisito = estado.ComoEstadoDeRequisito();
            ((int)deRequisito).Should().Be((int)estado);
            deRequisito.ToString().Should().Be(estado.ToString());
            deRequisito.ComoEstadoDocumento().Should().Be(estado);
        }

        Enum.GetValues<EstadoTalvegRequisito>().Should().HaveCount(Enum.GetValues<EstadoDocumento>().Length + 1);
        EstadoTalvegRequisito.NoAplica.ComoEstadoDocumento().Should().BeNull();
    }

    /// <summary>
    /// Separar el estado en TALVEG del de la Plataforma CAE no cambia el rótulo de hoy: para cualquier conjunto de estados
    /// documentales y cualquier causa de la Plataforma CAE, derivar de las dos columnas da lo mismo que la calculadora con
    /// todas las causas juntas. La causa «vencido en la plataforma» aporta además un Vencido, que no cambia nada porque es
    /// bloqueante.
    /// </summary>
    [Fact]
    public void Derivar_da_lo_mismo_que_la_calculadora_con_todas_las_causas()
    {
        var valores = Enum.GetValues<EstadoDocumento>();
        for (var mascara = 0; mascara < 1 << valores.Length; mascara++)
        {
            var estados = valores.Where((_, i) => (mascara & (1 << i)) != 0).ToList();
            var talveg = CalculadoraEstadoCentro.Calcular(estados, tieneRequisitoBloqueanteSinCumplir: false);

            foreach (var plataforma in new EstadoPlataformaCentro?[] { null, EstadoPlataformaCentro.Pendiente, EstadoPlataformaCentro.Bloqueado })
            {
                var bloqueado = plataforma == EstadoPlataformaCentro.Bloqueado;
                var conTodas = CalculadoraEstadoCentro.Calcular(
                    bloqueado ? [.. estados, EstadoDocumento.Vencido] : estados,
                    tieneRequisitoBloqueanteSinCumplir: bloqueado,
                    tienePendienteEnPlataforma: plataforma == EstadoPlataformaCentro.Pendiente);

                EstadoCentroCombinado.Derivar(talveg, plataforma)
                    .Should().Be(conTodas, $"estados [{string.Join(", ", estados)}] y plataforma {plataforma?.ToString() ?? "sin causa"}");
            }
        }
    }

    [Fact]
    public void Un_Centro_sin_gestion_CAE_sigue_sin_gestion_CAE()
    {
        EstadoCentroCombinado.Derivar(EstadoCentro.SinGestionCae, EstadoPlataformaCentro.Bloqueado).Should().Be(EstadoCentro.SinGestionCae);
    }
}
