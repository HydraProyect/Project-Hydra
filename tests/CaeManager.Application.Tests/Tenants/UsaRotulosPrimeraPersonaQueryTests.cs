using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Application.Tenants.Queries.UsaRotulosPrimeraPersona;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using MediatR;
using Xunit;

namespace CaeManager.Application.Tests.Tenants;

/// <summary>
/// DDL-072, decisión del propietario 2026-09-28: «Mi empresa» / «Mis trabajadores» solo si el
/// perfil del Tenant activo es Cliente Directo Y el Tenant de origen del usuario es el Tenant
/// activo. Usa el handler real del perfil, no un doble.
/// </summary>
public class UsaRotulosPrimeraPersonaQueryTests
{
    private static async Task<bool> Preguntar(
        PerfilVocabularioTenant perfilDelActivo, bool usuarioEsDelTenantActivo, Guid? tenantActualOverride = null,
        Guid? origenOverride = null, PerfilVocabularioTenant? perfilForzado = null)
    {
        var activo = new Tenant("Tenant activo", perfilDelActivo);
        var ajeno = new Tenant("Operador CAE externo", PerfilVocabularioTenant.Consultora);
        var contexto = new TenantsQueryContextFalso();
        contexto.ListaTenants.Add(activo);
        contexto.ListaTenants.Add(ajeno);

        var tenantActual = new TenantFijo(tenantActualOverride ?? activo.Id);
        var origen = origenOverride ?? (usuarioEsDelTenantActivo ? activo.Id : ajeno.Id);
        var perfil = new ObtenerPerfilVocabularioActualQueryHandler(contexto, tenantActual, new VistaFija(perfilForzado));
        var handler = new UsaRotulosPrimeraPersonaQueryHandler(
            new EmisorDePerfil(perfil), tenantActual, new CurrentUserServiceFalso(tenantOrigenId: origen));

        return await handler.Handle(new UsaRotulosPrimeraPersonaQuery(), CancellationToken.None);
    }

    [Fact]
    public async Task Usuario_del_Tenant_propietario_con_perfil_ClienteDirecto_habla_en_primera_persona() =>
        (await Preguntar(PerfilVocabularioTenant.ClienteDirecto, usuarioEsDelTenantActivo: true)).Should().BeTrue();

    [Fact]
    public async Task Operador_CAE_externo_en_un_Tenant_beneficiario_ClienteDirecto_no_habla_en_primera_persona() =>
        (await Preguntar(PerfilVocabularioTenant.ClienteDirecto, usuarioEsDelTenantActivo: false)).Should().BeFalse();

    [Fact]
    public async Task Perfil_Consultora_nunca_habla_en_primera_persona() =>
        (await Preguntar(PerfilVocabularioTenant.Consultora, usuarioEsDelTenantActivo: true)).Should().BeFalse();

    [Fact]
    public async Task Sin_Tenant_de_origen_falla_al_rotulo_neutro()
    {
        var activo = new Tenant("Tenant activo", PerfilVocabularioTenant.ClienteDirecto);
        var contexto = new TenantsQueryContextFalso();
        contexto.ListaTenants.Add(activo);
        var tenantActual = new TenantFijo(activo.Id);
        var handler = new UsaRotulosPrimeraPersonaQueryHandler(
            new EmisorDePerfil(new ObtenerPerfilVocabularioActualQueryHandler(contexto, tenantActual, new VistaFija(null))),
            tenantActual, new CurrentUserServiceFalso(tenantOrigenId: null));

        (await handler.Handle(new UsaRotulosPrimeraPersonaQuery(), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Sin_Tenant_activo_falla_al_rotulo_neutro()
    {
        var contexto = new TenantsQueryContextFalso();
        var tenantActual = new TenantFijo(null);
        var handler = new UsaRotulosPrimeraPersonaQueryHandler(
            new EmisorDePerfil(new ObtenerPerfilVocabularioActualQueryHandler(contexto, tenantActual, new VistaFija(null))),
            tenantActual, new CurrentUserServiceFalso(tenantOrigenId: Guid.NewGuid()));

        (await handler.Handle(new UsaRotulosPrimeraPersonaQuery(), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task La_vista_previa_del_Administrador_en_su_propio_Tenant_si_habla_en_primera_persona() =>
        (await Preguntar(
            PerfilVocabularioTenant.Consultora, usuarioEsDelTenantActivo: true,
            perfilForzado: PerfilVocabularioTenant.ClienteDirecto)).Should().BeTrue();

    private sealed class TenantFijo(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    private sealed class VistaFija(PerfilVocabularioTenant? perfil) : IVistaVocabularioPreviewService
    {
        public PerfilVocabularioTenant? PerfilForzado => perfil;
    }

    /// <summary>ISender mínimo: solo sabe responder la consulta de perfil, con el handler real.</summary>
    private sealed class EmisorDePerfil(ObtenerPerfilVocabularioActualQueryHandler perfil) : ISender
    {
        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            request is ObtenerPerfilVocabularioActualQuery q
                ? (TResponse)(object)await perfil.Handle(q, cancellationToken)
                : throw new NotSupportedException(request.GetType().Name);

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
