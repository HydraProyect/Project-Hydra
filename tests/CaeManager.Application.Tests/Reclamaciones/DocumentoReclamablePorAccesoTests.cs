using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Application.Reclamaciones.Queries.ObtenerReclamacionAEmpresaEmpleadora;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Reclamaciones;

/// <summary>
/// La reclamación por acceso pide lo vencido o pendiente: lo «Vigente» que la ventana de 3 meses de los lotes trae no se
/// reclama, y «Por vencer» (Próximo y Urgente) sigue dentro. Lo que la query hace con esta regla (en los documentos de los
/// Trabajadores y en los de la Empresa) lo prueba <c>ReclamacionConsolidadaEmpresaEmpleadoraTests</c> contra PostgreSQL.
/// </summary>
public class DocumentoReclamablePorAccesoTests
{
    [Fact]
    public void Lo_vigente_no_se_reclama_por_acceso()
    {
        DocumentoReclamablePorAcceso.SeReclama(EstadoDocumento.Vigente).Should().BeFalse();
    }

    [Theory]
    [InlineData(EstadoDocumento.Proximo)]
    [InlineData(EstadoDocumento.Urgente)]
    [InlineData(EstadoDocumento.Vencido)]
    [InlineData(EstadoDocumento.EnTolerancia)]
    public void Lo_vencido_y_lo_que_esta_por_vencer_si(EstadoDocumento estado)
    {
        DocumentoReclamablePorAcceso.SeReclama(estado).Should().BeTrue();
    }

    [Fact]
    public void Vigente_es_el_unico_estado_que_queda_fuera()
    {
        Enum.GetValues<EstadoDocumento>().Where(e => !DocumentoReclamablePorAcceso.SeReclama(e))
            .Should().Equal(EstadoDocumento.Vigente);
    }

    [Fact]
    public void Sobre_el_documento_mira_su_estado()
    {
        static DocumentoReclamableDto Documento(EstadoDocumento estado) => new(
            Guid.NewGuid(), Guid.NewGuid(), "Ana Pérez", Guid.NewGuid(), "Evaluación de riesgos", new DateOnly(2027, 1, 26), estado);

        DocumentoReclamablePorAcceso.SeReclama(Documento(EstadoDocumento.Vigente)).Should().BeFalse();
        DocumentoReclamablePorAcceso.SeReclama(Documento(EstadoDocumento.Proximo)).Should().BeTrue();
    }
}
