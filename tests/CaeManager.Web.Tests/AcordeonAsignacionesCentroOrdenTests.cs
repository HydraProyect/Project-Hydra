using Bunit;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Centros.Components;
using FluentAssertions;

namespace CaeManager.Web.Tests;

public partial class AcordeonAsignacionesCentroVigenciaSinConfirmarTests
{
    [Fact]
    public void Centro_360_ordena_asignaciones_del_peor_estado_al_mejor_como_la_maqueta()
    {
        EstadoDocumento[] estados = [EstadoDocumento.SinCaducidad, EstadoDocumento.Vigente,
            EstadoDocumento.SinConfirmar, EstadoDocumento.Proximo, EstadoDocumento.Urgente,
            EstadoDocumento.EnTolerancia, EstadoDocumento.Faltante, EstadoDocumento.Vencido];
        var trabajadores = estados.Select(estado => new TrabajadorAsignacionDocumentacionDto(
            Guid.NewGuid(), Guid.NewGuid(), estado.ToString(), DiaDeNegocio.Hoy(), estado,
            [new DocumentoRequeridoDto(Guid.NewGuid(), Guid.NewGuid(), "Documento", estado, null)],
            CumplimientoDocumental.Evaluar([estado]))).ToArray();
        var bloqueado = new TrabajadorAsignacionDocumentacionDto(Guid.NewGuid(), Guid.NewGuid(), "Bloqueado",
            DiaDeNegocio.Hoy(), EstadoDocumento.Faltante, [], CumplimientoDocumental.Evaluar([EstadoDocumento.Faltante]));
        var centroId = Guid.NewGuid();
        RegistrarServicios(new MediatorFalso { Trabajadores = [.. trabajadores, bloqueado] });
        var cut = Render<AcordeonAsignacionesCentro>(p => p.Add(c => c.CentroId, centroId)
            .Add(c => c.Bloqueos, new DocumentacionBloqueantePendienteDto[]
            {
                new(centroId, "Centro", bloqueado.TrabajadorId, bloqueado.TrabajadorNombre, Guid.NewGuid(), "Documento")
            }));

        cut.WaitForAssertion(() => cut.FindAll(".lista-trabajadores-acordeon .fila-relacion-nombre")
            .Select(f => f.TextContent.Trim()).Should().Equal(
                "Bloqueado", "Vencido", "Faltante", "EnTolerancia", "Urgente", "Proximo", "SinConfirmar", "Vigente", "SinCaducidad"));
    }
}
