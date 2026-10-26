using CaeManager.Application.BusquedaGlobal.Commands.RegistrarUsoReciente;
using CaeManager.Application.Common;
using CaeManager.Domain.BusquedaGlobal;
using FluentAssertions;

namespace CaeManager.Application.Tests.BusquedaGlobal;

public class RegistrarUsoRecienteCommandHandlerTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/documentos")]
    [InlineData("/documentos?filtro=urgente#seccion")]
    public async Task Una_ruta_local_se_guarda_intacta(string destino)
    {
        var persistencia = new PersistenciaFalsa();
        var usuario = new UsuarioFalso();
        var handler = new RegistrarUsoRecienteCommandHandler(persistencia, persistencia, usuario);

        var resultado = await handler.Handle(new("Documento", null, "Documento", null, destino), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        persistencia.Eventos.Should().ContainSingle().Which.UrlDestino.Should().Be(destino);
        persistencia.Purgas.Should().Be(1);
        persistencia.Guardados.Should().Be(1);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("/\u0000/evil.example")]
    [InlineData("/\u0009/evil.example")]
    [InlineData("/\u007F/evil.example")]
    [InlineData("/\u0085/evil.example")]
    [InlineData("/documentos\u0000")]
    [InlineData("/documentos\u0009")]
    [InlineData("/documentos\u007F")]
    [InlineData("/documentos\u0085")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("documentos")]
    public async Task Un_destino_inseguro_devuelve_error_de_validacion_sin_persistir(string? destino)
    {
        var persistencia = new PersistenciaFalsa();
        var handler = new RegistrarUsoRecienteCommandHandler(persistencia, persistencia, new UsuarioFalso());

        var resultado = await handler.Handle(new("Documento", null, "Documento", null, destino!), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Reciente.UrlDestinoInvalida");
        persistencia.Eventos.Should().BeEmpty();
        persistencia.Purgas.Should().Be(0);
        persistencia.Guardados.Should().Be(0);
    }

    [Fact]
    public async Task Un_destino_inseguro_tambien_se_rechaza_sin_usuario_actual()
    {
        var persistencia = new PersistenciaFalsa();
        var handler = new RegistrarUsoRecienteCommandHandler(persistencia, persistencia, new UsuarioFalso(conUsuario: false));

        var resultado = await handler.Handle(new("Documento", null, "Documento", null, "//evil.example"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Reciente.UrlDestinoInvalida");
        persistencia.Eventos.Should().BeEmpty();
    }

    private sealed class PersistenciaFalsa : IEventoRecienteUsuarioRepository, IUnitOfWork
    {
        public List<EventoRecienteUsuario> Eventos { get; } = [];
        public int Purgas { get; private set; }
        public int Guardados { get; private set; }
        public void Agregar(EventoRecienteUsuario evento) => Eventos.Add(evento);
        public Task PurgarExcedentesAsync(Guid usuarioId, int maximoAConservar, CancellationToken cancellationToken)
        {
            Purgas++;
            return Task.CompletedTask;
        }
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(++Guardados);
    }

    private sealed class UsuarioFalso(bool conUsuario = true) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(conUsuario ? Guid.NewGuid() : null);
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(null);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult<string?>(null);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(null);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(false);
    }
}
