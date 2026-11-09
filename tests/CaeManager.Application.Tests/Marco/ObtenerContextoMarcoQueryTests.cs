using CaeManager.Application.Marco.Queries.ObtenerContextoMarco;
using CaeManager.Application.Operaciones.IncorporacionCartera.Queries;
using CaeManager.Application.Plataforma.OrdenMenu;
using CaeManager.Application.Tenants.Queries.EsAdministradorPlataforma;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Application.Tenants.Queries.UsaRotulosPrimeraPersona;
using CaeManager.Application.Usuarios.Queries.ObtenerAvatarPropio;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using MediatR;
using Xunit;

namespace CaeManager.Application.Tests.Marco;

/// <summary>
/// Pieza 1.3 del plan de rendimiento: el contexto del marco compone las mismas Queries que antes despachaba cada
/// componente, sin las variantes que fallan cerradas, y solo tolera que fallen las dos partes opcionales: la
/// participación en la incorporación a cartera (sin ella no se ofrece) y el avatar.
/// </summary>
public class ObtenerContextoMarcoQueryTests
{
    private static readonly ClienteAutorizadoDto Propio = new(Guid.NewGuid(), "Operador CAE de prueba", EsOrigen: true);
    private static readonly ClienteAutorizadoDto Beneficiario = new(Guid.NewGuid(), "Tenant beneficiario", EsOrigen: false);

    private sealed class Emisor(Func<object, object?> responder) : ISender
    {
        public List<object> Pedidas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Pedidas.Add(request);
            return Task.FromResult((TResponse)responder(request)!);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static object? Respuestas(object request) => request switch
    {
        ObtenerPerfilVocabularioActualQuery => PerfilVocabularioTenant.ClienteDirecto,
        UsaRotulosPrimeraPersonaQuery => true,
        EsAdministradorPlataformaQuery => true,
        ObtenerClientesAutorizadosQuery => (IReadOnlyList<ClienteAutorizadoDto>)[Propio, Beneficiario],
        ObtenerOrdenMenuLateralQuery => new OrdenMenuLateralDto(["plataforma", "documentos"], ["/empresas"], Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow),
        ParticipaEnIncorporacionCarteraQuery => true,
        ObtenerAvatarPropioQuery => "avatar-07",
        _ => throw new NotSupportedException(request.GetType().Name),
    };

    private static Task<ContextoMarcoDto> Componer(ISender emisor, CancellationToken cancellationToken = default) =>
        new ObtenerContextoMarcoQueryHandler(emisor).Handle(new ObtenerContextoMarcoQuery(), cancellationToken);

    [Fact]
    public async Task Compone_lo_que_devuelve_cada_Query_del_marco()
    {
        var contexto = await Componer(new Emisor(Respuestas));

        contexto.PerfilVocabulario.Should().Be(PerfilVocabularioTenant.ClienteDirecto);
        contexto.RotulosPrimeraPersona.Should().BeTrue();
        contexto.EsAdministradorPlataforma.Should().BeTrue();
        contexto.ClientesAutorizados.Should().Equal(Propio, Beneficiario);
        contexto.OrdenGrupos.Should().Equal("plataforma", "documentos");
        contexto.OrdenEnlaces.Should().Equal("/empresas");
        contexto.ParticipaEnIncorporacionCartera.Should().BeTrue();
        contexto.Avatar.Should().Be("avatar-07");
    }

    [Fact]
    public async Task No_pide_las_variantes_que_fallan_cerradas()
    {
        var emisor = new Emisor(Respuestas);

        await Componer(emisor);

        // El dato vigente lo leen las puertas por su camino; el marco solo decide qué se ofrece (ASVS 4.0.3 V4.1.1).
        emisor.Pedidas.OfType<EsAdministradorPlataformaQuery>().Should().ContainSingle().Which.LecturaVigente.Should().BeFalse();
        emisor.Pedidas.OfType<ObtenerClientesAutorizadosQuery>().Should().ContainSingle().Which.LecturaVigente.Should().BeFalse();
        emisor.Pedidas.Should().HaveCount(7, "una Query por dato del marco, ninguna repetida");
    }

    [Fact]
    public async Task Sin_orden_guardado_el_menu_usa_el_canonico()
    {
        var contexto = await Componer(new Emisor(r => r is ObtenerOrdenMenuLateralQuery ? null : Respuestas(r)));

        contexto.OrdenGrupos.Should().BeNull();
        contexto.OrdenEnlaces.Should().BeNull();
    }

    [Fact]
    public async Task Si_el_avatar_no_se_puede_leer_el_marco_se_pinta_sin_el()
    {
        var contexto = await Componer(new Emisor(r =>
            r is ObtenerAvatarPropioQuery ? throw new InvalidOperationException("sin conexión") : Respuestas(r)));

        contexto.Avatar.Should().BeNull();
        contexto.ClientesAutorizados.Should().Equal(Propio, Beneficiario);
    }

    [Fact]
    public async Task Si_la_participacion_no_se_puede_leer_no_se_ofrece_y_no_se_lee_nada_mas()
    {
        var emisor = new Emisor(r =>
            r is ParticipaEnIncorporacionCarteraQuery ? throw new InvalidOperationException("sin conexión") : Respuestas(r));

        var contexto = await Componer(emisor);

        contexto.ParticipaEnIncorporacionCartera.Should().BeFalse("sin la señal no se ofrece «Solicitar incorporación»");
        contexto.Avatar.Should().BeNull();
        contexto.ClientesAutorizados.Should().Equal(Propio, Beneficiario);
        emisor.Pedidas.OfType<ObtenerAvatarPropioQuery>().Should().BeEmpty("tras un fallo no se lee nada más del ámbito");
    }

    [Fact]
    public async Task Cualquier_otro_fallo_tumba_la_lectura_entera()
    {
        // Sin valores por defecto inventados: un marco a medias ofrecería lo que no toca.
        var componer = () => Componer(new Emisor(r =>
            r is ObtenerClientesAutorizadosQuery ? throw new InvalidOperationException("sin conexión") : Respuestas(r)));

        await componer.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task La_cancelacion_se_propaga_tambien_en_el_avatar()
    {
        var componer = () => Componer(new Emisor(r =>
            r is ObtenerAvatarPropioQuery ? throw new OperationCanceledException() : Respuestas(r)));

        await componer.Should().ThrowAsync<OperationCanceledException>();
    }
}
