using CaeManager.Application.Documentos.Commands.ConfirmarVigenciaAcreditacion;
using CaeManager.Application.Documentos.Commands;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// «Anotar vigencia» solo escribe sobre una acreditación Aceptada. La pantalla (fila o bloque) solo ofrece la acción
/// para las Aceptadas, pero entre que se abre el modal y se guarda alguien puede rechazarla o devolverla a pendiente:
/// la precondición vive en el servidor, no en lo que la pantalla vio.
/// </summary>
public class ConfirmarVigenciaAcreditacionCommandHandlerTests
{
    private static readonly VigenciaEnPlataforma Nueva = VigenciaEnPlataforma.VenceEl(new DateOnly(2027, 3, 14));

    private sealed class Mundo
    {
        public Documento Documento { get; } = Documento.DeTrabajador(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca);
        public AcreditacionDocumentoPlataforma Acreditacion { get; }
        public UnitOfWorkFalso UnitOfWork { get; } = new();
        private readonly DocumentoRepositorioFalso _documentos = new();
        private readonly AcreditacionDocumentoPlataformaRepositorioFalso _acreditaciones = new();

        public Mundo()
        {
            Acreditacion = new AcreditacionDocumentoPlataforma(Documento.Id, Guid.NewGuid());
            _documentos.Agregar(Documento);
            _acreditaciones.Agregar(Acreditacion);
        }

        public Task<Result<ResultadoAnotacionAcreditacionDto>> AnotarAsync() =>
            new ConfirmarVigenciaAcreditacionCommandHandler(_acreditaciones, _documentos, new AlcanceDatosServiceFalso(),
                    new ProyectosQueryContextFalso(), new CentrosQueryContextFalso(), UnitOfWork)
                .Handle(new ConfirmarVigenciaAcreditacionCommand(Acreditacion.Id, Nueva), CancellationToken.None);
    }

    [Theory]
    [InlineData("pendiente")]
    [InlineData("subida")]
    [InlineData("rechazada")]
    public async Task Sobre_una_acreditacion_que_no_esta_aceptada_falla_y_no_escribe(string estado)
    {
        var m = new Mundo();
        if (estado is "subida" or "rechazada") m.Acreditacion.MarcarSubida();
        if (estado == "rechazada") m.Acreditacion.Rechazar(CausaRechazoAcreditacion.Ilegible, "Firma ilegible", DateTime.UtcNow);
        var estadoAntes = m.Acreditacion.Estado;
        var versionAntes = m.Acreditacion.Version;

        var r = await m.AnotarAsync();

        r.EsFallido.Should().BeTrue(estado);
        r.Error.Codigo.Should().Be("Acreditacion.NoAceptada");
        m.Acreditacion.Estado.Should().Be(estadoAntes);
        m.Acreditacion.Vigencia.EstaSinConfirmar.Should().BeTrue("no se anota nada");
        m.Acreditacion.Version.Should().Be(versionAntes);
        m.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Sobre_una_acreditacion_aceptada_anota_la_vigencia_y_guarda()
    {
        var m = new Mundo();
        m.Acreditacion.MarcarAceptada(VigenciaEnPlataforma.SinConfirmar);

        var r = await m.AnotarAsync();

        r.EsExitoso.Should().BeTrue();
        m.Acreditacion.Estado.Should().Be(EstadoAcreditacion.Aceptada);
        m.Acreditacion.Vigencia.Should().Be(Nueva);
        m.UnitOfWork.VecesGuardado.Should().Be(1);
    }
}
