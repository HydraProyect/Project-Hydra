using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Centros;

public class CalculadoraEstadoCentroTests
{
    [Fact]
    public void Retorna_Vigente_cuando_no_hay_documentos_ni_requisitos_bloqueantes()
    {
        var estado = CalculadoraEstadoCentro.Calcular([], tieneRequisitoBloqueanteSinCumplir: false);

        estado.Should().Be(EstadoCentro.Vigente);
    }

    [Fact]
    public void Retorna_Bloqueado_aunque_toda_la_documentacion_este_vigente()
    {
        var estado = CalculadoraEstadoCentro.Calcular(
            [EstadoDocumento.Vigente], tieneRequisitoBloqueanteSinCumplir: true);

        estado.Should().Be(EstadoCentro.Bloqueado);
    }

    [Fact]
    public void Retorna_Faltante_cuando_algun_trabajador_no_tiene_un_documento_obligatorio()
    {
        var estado = CalculadoraEstadoCentro.Calcular(
            [EstadoDocumento.Vigente, EstadoDocumento.Faltante], tieneRequisitoBloqueanteSinCumplir: false);

        estado.Should().Be(EstadoCentro.Faltante);
    }

    [Fact]
    public void Retorna_el_peor_estado_de_vigencia_entre_todos_los_documentos()
    {
        var estado = CalculadoraEstadoCentro.Calcular(
            [EstadoDocumento.Vigente, EstadoDocumento.Proximo, EstadoDocumento.Vencido, EstadoDocumento.Urgente],
            tieneRequisitoBloqueanteSinCumplir: false);

        estado.Should().Be(EstadoCentro.Vencido);
    }

    [Theory]
    [InlineData(EstadoDocumento.Urgente, EstadoCentro.Urgente)]
    [InlineData(EstadoDocumento.Proximo, EstadoCentro.Proximo)]
    [InlineData(EstadoDocumento.Vigente, EstadoCentro.Vigente)]
    public void Retorna_el_estado_correspondiente_al_unico_documento(EstadoDocumento estadoDocumento, EstadoCentro esperado)
    {
        var estado = CalculadoraEstadoCentro.Calcular([estadoDocumento], tieneRequisitoBloqueanteSinCumplir: false);

        estado.Should().Be(esperado);
    }

    // Orden único en todas las superficies (decisión del propietario, 2026-10-03): Bloqueante → Vencido → Faltante.
    // Las dos filas dan los mismos estados en distinto orden: el resultado no depende de cuál llegue primero.
    [Theory]
    [InlineData(EstadoDocumento.Vencido, EstadoDocumento.Faltante)]
    [InlineData(EstadoDocumento.Faltante, EstadoDocumento.Vencido)]
    public void Un_Centro_con_un_documento_vencido_y_otro_faltante_esta_Vencido(EstadoDocumento primero, EstadoDocumento segundo)
    {
        var estado = CalculadoraEstadoCentro.Calcular(
            [EstadoDocumento.Vigente, primero, segundo, EstadoDocumento.Urgente], tieneRequisitoBloqueanteSinCumplir: false);

        estado.Should().Be(EstadoCentro.Vencido);
    }

    [Fact]
    public void El_bloqueo_de_la_plataforma_del_Cliente_empresarial_prevalece_sobre_lo_vencido_y_lo_faltante()
    {
        var estado = CalculadoraEstadoCentro.Calcular(
            [EstadoDocumento.Faltante, EstadoDocumento.Vencido], tieneRequisitoBloqueanteSinCumplir: true);

        estado.Should().Be(EstadoCentro.Bloqueado);
    }

    [Theory]
    [InlineData(EstadoDocumento.Urgente)]
    [InlineData(EstadoDocumento.Proximo)]
    public void Un_documento_faltante_prevalece_sobre_lo_que_esta_por_vencer(EstadoDocumento porVencer)
    {
        var estado = CalculadoraEstadoCentro.Calcular(
            [porVencer, EstadoDocumento.Faltante], tieneRequisitoBloqueanteSinCumplir: false);

        estado.Should().Be(EstadoCentro.Faltante);
    }

    [Fact]
    public void La_gravedad_ordena_los_Centros_con_el_mismo_orden_que_decide_su_estado()
    {
        // El valor numérico del enum está congelado (API v1) y tiene Faltante por encima de Vencido: la clave de
        // orden no puede ser ese número. SinGestionCae no es un grado de incumplimiento y va detrás de Vigente.
        var dePeorAMejor = Enum.GetValues<EstadoCentro>().OrderByDescending(CalculadoraEstadoCentro.Gravedad);

        dePeorAMejor.Should().Equal(
            EstadoCentro.Bloqueado, EstadoCentro.Vencido, EstadoCentro.Faltante, EstadoCentro.Pendiente,
            EstadoCentro.Urgente, EstadoCentro.Proximo, EstadoCentro.Vigente, EstadoCentro.SinGestionCae);
    }

    // --- Pendiente en la plataforma CAE del Centro (decisión del propietario, 2026-10-10) ---

    [Fact]
    public void Un_documento_pendiente_en_la_plataforma_pone_el_Centro_Pendiente()
    {
        CalculadoraEstadoCentro.Calcular([EstadoDocumento.Vigente], tieneRequisitoBloqueanteSinCumplir: false, tienePendienteEnPlataforma: true)
            .Should().Be(EstadoCentro.Pendiente);
    }

    [Fact]
    public void Sin_plataforma_no_hay_Pendiente_y_el_Centro_no_cambia()
    {
        // Un Centro sin plataforma nunca trae un pendiente: su estado es el de siempre.
        CalculadoraEstadoCentro.Calcular([EstadoDocumento.Vigente], tieneRequisitoBloqueanteSinCumplir: false, tienePendienteEnPlataforma: false)
            .Should().Be(EstadoCentro.Vigente);
        CalculadoraEstadoCentro.Calcular([EstadoDocumento.Vigente], tieneRequisitoBloqueanteSinCumplir: false)
            .Should().Be(EstadoCentro.Vigente, "el valor por defecto es «sin pendiente»");
    }

    [Theory]
    [InlineData(EstadoDocumento.Urgente)]
    [InlineData(EstadoDocumento.Proximo)]
    [InlineData(EstadoDocumento.SinConfirmar)]
    public void Pendiente_en_la_plataforma_prevalece_sobre_lo_que_esta_por_vencer(EstadoDocumento porVencer)
    {
        CalculadoraEstadoCentro.Calcular([porVencer], tieneRequisitoBloqueanteSinCumplir: false, tienePendienteEnPlataforma: true)
            .Should().Be(EstadoCentro.Pendiente);
    }

    [Theory]
    [InlineData(EstadoDocumento.Faltante, EstadoCentro.Faltante)]
    [InlineData(EstadoDocumento.Vencido, EstadoCentro.Vencido)]
    public void Lo_faltante_y_lo_vencido_prevalecen_sobre_el_Pendiente_en_la_plataforma(EstadoDocumento peor, EstadoCentro esperado)
    {
        CalculadoraEstadoCentro.Calcular([peor], tieneRequisitoBloqueanteSinCumplir: false, tienePendienteEnPlataforma: true)
            .Should().Be(esperado);
    }

    [Fact]
    public void El_bloqueo_de_la_plataforma_prevalece_sobre_el_Pendiente()
    {
        CalculadoraEstadoCentro.Calcular([], tieneRequisitoBloqueanteSinCumplir: true, tienePendienteEnPlataforma: true)
            .Should().Be(EstadoCentro.Bloqueado, "Rechazada sigue siendo causa propia de Bloqueado (D-7), por encima del Pendiente");
    }

    [Fact]
    public void Una_vigencia_sin_confirmar_no_da_color_al_centro()
    {
        // El semáforo refleja lo malo conocido; «sin confirmar» no se sabe malo.
        CalculadoraEstadoCentro.Calcular([EstadoDocumento.SinConfirmar], tieneRequisitoBloqueanteSinCumplir: false)
            .Should().Be(EstadoCentro.Vigente);
        CalculadoraEstadoCentro.Calcular([EstadoDocumento.SinConfirmar, EstadoDocumento.Proximo], tieneRequisitoBloqueanteSinCumplir: false)
            .Should().Be(EstadoCentro.Proximo, "no tapa lo malo conocido de otro documento");
    }
}
