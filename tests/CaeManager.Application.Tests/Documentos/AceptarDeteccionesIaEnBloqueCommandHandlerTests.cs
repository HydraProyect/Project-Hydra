using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.AceptarDeteccionesIaEnBloque;
using CaeManager.Application.Documentos.Commands.AplicarDeteccionIaDocumento;
using CaeManager.Domain.Common;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// El bloque nunca decide por sí solo qué aceptar: despacha, por el pipeline normal, un
/// <see cref="AplicarDeteccionIaDocumentoCommand"/> por cada Id recibido, con resultado por elemento, y ningún
/// elemento tumba a los demás.
/// </summary>
public class AceptarDeteccionesIaEnBloqueCommandHandlerTests
{
    private sealed class MediadorFalso(Func<AplicarDeteccionIaDocumentoCommand, Result> alAplicar) : IMediator
    {
        public List<AplicarDeteccionIaDocumentoCommand> Aplicadas { get; } = [];

        public Task<T> Send<T>(IRequest<T> request, CancellationToken cancellationToken = default)
        {
            var comando = (AplicarDeteccionIaDocumentoCommand)(object)request;
            Aplicadas.Add(comando);
            return Task.FromResult((T)(object)alAplicar(comando));
        }

        public Task Send<T>(T request, CancellationToken cancellationToken = default) where T : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<T> CreateStream<T>(IStreamRequest<T> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish<T>(T notification, CancellationToken cancellationToken = default) where T : INotification => throw new NotSupportedException();
    }

    private sealed class DescarteFalso : IDescarteCambiosPendientes
    {
        public int Descartes { get; private set; }
        public void DescartarCambiosPendientes() => Descartes++;
    }

    private static AceptarDeteccionesIaEnBloqueCommandHandler Crear(MediadorFalso mediador, DescarteFalso? descarte = null) =>
        new(mediador, descarte ?? new DescarteFalso(), NullLogger<AceptarDeteccionesIaEnBloqueCommandHandler>.Instance);

    [Fact]
    public async Task Despacha_un_comando_por_Id_con_el_filtro_de_vigencia_y_agrupa_el_resultado_por_elemento()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var mediador = new MediadorFalso(cmd => cmd.RevisionId == b
            ? Result.Fallo(Error.Crear("RevisionIa.NoEncontrada", "No encontramos esta revisión."))
            : Result.Exito());

        var resultado = await Crear(mediador).Handle(new AceptarDeteccionesIaEnBloqueCommand([a, b, c]), CancellationToken.None);

        mediador.Aplicadas.Select(x => x.RevisionId).Should().ContainInOrder([a, b, c], "uno por Id, en orden, sin abortar por el segundo");
        mediador.Aplicadas.Should().HaveCount(3);
        mediador.Aplicadas.Should().OnlyContain(x => x.SoloSiLaVigenciaLaFijaElTipo,
            "en bloque solo se acepta lo que el tipo calcula; la vigencia a mano se acepta de una en una");
        resultado.Valor.Aceptadas.Should().Be(2);
        resultado.Valor.Fallidas.Should().Be(1);
        var fallida = resultado.Valor.Resultados.Single(r => !r.Aceptada);
        fallida.RevisionId.Should().Be(b);
        fallida.CodigoError.Should().Be("RevisionIa.NoEncontrada");
    }

    [Fact]
    public async Task Un_resultado_fallido_tambien_descarta_lo_pendiente_para_no_contaminar_al_siguiente()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var descarte = new DescarteFalso();
        var mediador = new MediadorFalso(cmd => cmd.RevisionId == a
            ? Result.Fallo(Error.Crear("Concurrencia.Conflicto", "Otra persona modificó este registro."))
            : Result.Exito());

        var resultado = await Crear(mediador, descarte).Handle(new AceptarDeteccionesIaEnBloqueCommand([a, b]), CancellationToken.None);

        descarte.Descartes.Should().Be(1, "solo el elemento fallido descarta; un éxito ya está guardado y no se toca");
        resultado.Valor.Resultados.Single(r => r.RevisionId == b).Aceptada.Should().BeTrue();
    }

    [Fact]
    public async Task Un_Id_repetido_o_vacio_no_se_acepta_dos_veces()
    {
        var a = Guid.NewGuid();
        var mediador = new MediadorFalso(_ => Result.Exito());

        var resultado = await Crear(mediador).Handle(new AceptarDeteccionesIaEnBloqueCommand([a, a, Guid.Empty]), CancellationToken.None);

        mediador.Aplicadas.Should().ContainSingle().Which.RevisionId.Should().Be(a);
        resultado.Valor.Resultados.Should().ContainSingle();
    }

    [Fact]
    public async Task Un_bloque_vacio_o_demasiado_grande_se_rechaza_sin_despachar_nada()
    {
        var mediador = new MediadorFalso(_ => Result.Exito());
        var vacio = await Crear(mediador).Handle(new AceptarDeteccionesIaEnBloqueCommand([]), CancellationToken.None);
        var grande = await Crear(mediador).Handle(
            new AceptarDeteccionesIaEnBloqueCommand(Enumerable.Range(0, AceptarDeteccionesIaEnBloqueCommandHandler.MaximoRevisionesPorBloque + 1)
                .Select(_ => Guid.NewGuid()).ToList()), CancellationToken.None);

        vacio.EsFallido.Should().BeTrue();
        vacio.Error.Codigo.Should().Be("RevisionIa.BloqueVacio");
        grande.EsFallido.Should().BeTrue();
        grande.Error.Codigo.Should().Be("RevisionIa.BloqueDemasiadoGrande");
        mediador.Aplicadas.Should().BeEmpty("un bloque rechazado no acepta nada");
    }

    [Fact]
    public async Task Una_excepcion_en_un_elemento_descarta_lo_pendiente_y_no_impide_aceptar_los_demas()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var mediador = new MediadorFalso(cmd => cmd.RevisionId == a ? throw new InvalidOperationException("boom") : Result.Exito());
        var descarte = new DescarteFalso();

        var resultado = await Crear(mediador, descarte).Handle(new AceptarDeteccionesIaEnBloqueCommand([a, b]), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        descarte.Descartes.Should().Be(1, "el guardado fallido no puede contaminar al elemento siguiente");
        resultado.Valor.Resultados.Single(r => r.RevisionId == a).CodigoError.Should().Be("RevisionIa.ErrorInesperado");
        resultado.Valor.Resultados.Single(r => r.RevisionId == b).Aceptada.Should().BeTrue();
    }
}
