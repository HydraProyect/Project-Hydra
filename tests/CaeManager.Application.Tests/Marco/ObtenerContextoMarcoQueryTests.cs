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
/// componente, sin las variantes que fallan cerradas. Cada parte de presentación tolera su fallo con un valor que no
/// ofrece nada y marca el contexto como degradado; la lista de Tenants beneficiarios autorizados no se tolera.
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
        ObtenerPerfilVocabularioActualQuery => PerfilVocabularioTenant.Consultora,
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

        contexto.PerfilVocabulario.Should().Be(PerfilVocabularioTenant.Consultora);
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
    public async Task Sin_fallos_no_va_marcado_como_degradado()
    {
        (await Componer(new Emisor(Respuestas))).Degradado.Should().BeFalse();
    }

    private static Func<object, object?> FallaEn<TQuery>() =>
        r => r is TQuery ? throw new InvalidOperationException("sin conexión") : Respuestas(r);

    [Fact]
    public async Task Si_el_perfil_de_vocabulario_falla_se_pinta_con_el_perfil_por_defecto_y_no_se_lee_nada_mas()
    {
        var emisor = new Emisor(FallaEn<ObtenerPerfilVocabularioActualQuery>());

        var contexto = await Componer(emisor);

        contexto.PerfilVocabulario.Should().Be(PerfilVocabularioTenant.ClienteDirecto);
        contexto.ClientesAutorizados.Should().Equal(Propio, Beneficiario);
        contexto.Degradado.Should().BeTrue();
        // Tras el primer fallo, el resto de la presentación toma su valor sin oferta sin leer (DbContext del ámbito).
        contexto.RotulosPrimeraPersona.Should().BeFalse();
        contexto.EsAdministradorPlataforma.Should().BeFalse();
        contexto.OrdenGrupos.Should().BeNull();
        contexto.ParticipaEnIncorporacionCartera.Should().BeFalse();
        contexto.Avatar.Should().BeNull();
        emisor.Pedidas.Should().HaveCount(2, "la lista de Tenants beneficiarios y el perfil que falló; nada después");
    }

    [Fact]
    public async Task Si_los_rotulos_fallan_no_se_ofrece_la_primera_persona()
    {
        var contexto = await Componer(new Emisor(FallaEn<UsaRotulosPrimeraPersonaQuery>()));

        contexto.RotulosPrimeraPersona.Should().BeFalse();
        contexto.PerfilVocabulario.Should().Be(PerfilVocabularioTenant.Consultora, "lo leído antes del fallo se conserva");
        contexto.ClientesAutorizados.Should().Equal(Propio, Beneficiario);
        contexto.Degradado.Should().BeTrue();
    }

    [Fact]
    public async Task Si_la_senal_de_AdminPlataforma_falla_no_se_ofrece_el_grupo_Plataforma()
    {
        var contexto = await Componer(new Emisor(FallaEn<EsAdministradorPlataformaQuery>()));

        contexto.EsAdministradorPlataforma.Should().BeFalse();
        contexto.RotulosPrimeraPersona.Should().BeTrue("lo leído antes del fallo se conserva");
        contexto.ClientesAutorizados.Should().Equal(Propio, Beneficiario);
        contexto.Degradado.Should().BeTrue();
    }

    [Fact]
    public async Task Si_el_orden_del_menu_falla_se_usa_el_canonico()
    {
        var contexto = await Componer(new Emisor(FallaEn<ObtenerOrdenMenuLateralQuery>()));

        contexto.OrdenGrupos.Should().BeNull();
        contexto.OrdenEnlaces.Should().BeNull();
        contexto.EsAdministradorPlataforma.Should().BeTrue("lo leído antes del fallo se conserva");
        contexto.ClientesAutorizados.Should().Equal(Propio, Beneficiario);
        contexto.Degradado.Should().BeTrue();
    }

    [Fact]
    public async Task Si_la_participacion_no_se_puede_leer_no_se_ofrece_y_no_se_lee_nada_mas()
    {
        var emisor = new Emisor(FallaEn<ParticipaEnIncorporacionCarteraQuery>());

        var contexto = await Componer(emisor);

        contexto.ParticipaEnIncorporacionCartera.Should().BeFalse("sin la señal no se ofrece «Solicitar incorporación»");
        contexto.Avatar.Should().BeNull();
        contexto.OrdenGrupos.Should().Equal("plataforma", "documentos");
        contexto.ClientesAutorizados.Should().Equal(Propio, Beneficiario);
        contexto.Degradado.Should().BeTrue();
        emisor.Pedidas.OfType<ObtenerAvatarPropioQuery>().Should().BeEmpty("tras un fallo no se lee nada más del ámbito");
    }

    [Fact]
    public async Task Si_el_avatar_no_se_puede_leer_el_marco_se_pinta_sin_el()
    {
        var contexto = await Componer(new Emisor(FallaEn<ObtenerAvatarPropioQuery>()));

        contexto.Avatar.Should().BeNull();
        contexto.ParticipaEnIncorporacionCartera.Should().BeTrue("lo leído antes del fallo se conserva");
        contexto.ClientesAutorizados.Should().Equal(Propio, Beneficiario);
        contexto.Degradado.Should().BeTrue();
    }

    [Fact]
    public async Task Un_fallo_de_la_lista_de_Tenants_beneficiarios_no_se_tolera_y_tumba_la_lectura_entera()
    {
        // Es el dato de autorización (como oferta) del marco: sin valores inventados, fail-closed.
        var emisor = new Emisor(FallaEn<ObtenerClientesAutorizadosQuery>());
        var componer = () => Componer(emisor);

        await componer.Should().ThrowAsync<InvalidOperationException>();
        emisor.Pedidas.Should().ContainSingle("la lista va primero y, si falla, no se lee nada más")
            .Which.Should().BeOfType<ObtenerClientesAutorizadosQuery>();
    }

    [Fact]
    public async Task La_cancelacion_se_propaga_tambien_en_una_parte_tolerada()
    {
        var componer = () => Componer(new Emisor(r =>
            r is ObtenerPerfilVocabularioActualQuery ? throw new OperationCanceledException() : Respuestas(r)));

        await componer.Should().ThrowAsync<OperationCanceledException>();
    }
}
