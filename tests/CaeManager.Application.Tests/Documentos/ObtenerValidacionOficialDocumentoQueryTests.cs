using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerValidacionOficialDocumento;
using CaeManager.Domain.Documentos;
using FluentAssertions;

namespace CaeManager.Application.Tests.Documentos;

public class ObtenerValidacionOficialDocumentoQueryTests
{
    private sealed class Seleccion(bool soporte) : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => soporte ? Guid.NewGuid() : null;
    }

    [Theory]
    [InlineData(true, "CODIGO", false, null)]
    [InlineData(false, "CODIGO", true, "CODIGO")]
    [InlineData(false, null, true, null)]
    public async Task Distingue_codigo_no_disponible_de_codigo_ausente(bool soporte, string? codigo, bool disponible, string? esperado)
    {
        var id = Guid.NewGuid();
        var contexto = new DocumentosQueryContextFalso();
        contexto.ListaVerificacionesDocumentoOficial.Add(new VerificacionDocumentoOficial(
            id, PerfilDocumentoOficial.CorrienteTgss, NivelConfianzaDocumental.FirmaValidaSinRevocacion,
            codigo, null, null, null, null, ResultadoCotejoDocumentoOficial.Coincide,
            DecisionValidacionOficial.AutoValidado, ""));
        var resultado = await new ObtenerValidacionOficialDocumentoQueryHandler(contexto, new Seleccion(soporte))
            .Handle(new(id), CancellationToken.None);
        resultado.Should().NotBeNull();
        resultado!.CodigoVerificacionDisponible.Should().Be(disponible);
        resultado.CodigoVerificacion.Should().Be(esperado);
        resultado.Perfil.Should().Be(PerfilDocumentoOficial.CorrienteTgss);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sin_verificacion_devuelve_null(bool soporte)
    {
        var resultado = await new ObtenerValidacionOficialDocumentoQueryHandler(new DocumentosQueryContextFalso(), new Seleccion(soporte))
            .Handle(new(Guid.NewGuid()), CancellationToken.None);
        resultado.Should().BeNull();
    }
}
