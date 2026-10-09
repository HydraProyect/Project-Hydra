using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Usuarios.Queries.ObtenerPersonasConCartera;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.Empresas.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Dato de cabecera «Gestor CAE» de la pantalla Empresas (decisión 2026-10-08): quién gestiona
/// el Tenant propietario activo — el Gestor CAE principal y los Gestores CAE de apoyo, que son
/// personas (el Operador CAE es la organización). Solo lectura: dar acceso de apoyo,
/// desasignarse, revocar y «Asumir» son otras líneas. Qué puede ver cada quien lo decide
/// <c>ObtenerPersonasConCarteraQuery</c> y se prueba en Application; aquí, qué se pinta con lo
/// que esa consulta devuelve.
/// </summary>
public class CabeceraGestorCaeTests : BunitContext
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid OtroTenant = Guid.NewGuid();

    private sealed class TenantFijo(Guid? id) : ITenantActual
    {
        public Guid? TenantId { get; } = id;
    }

    private IRenderedComponent<CabeceraGestorCae> Renderizar(
        Func<ObtenerPersonasConCarteraQuery, IReadOnlyList<CarterasDeOperacion>> responder, Guid? tenant = null, bool sinTenant = false)
    {
        Services.AddLocalization();
        Services.AddScoped<ITenantActual>(_ => new TenantFijo(sinTenant ? null : tenant ?? Tenant));
        Services.AddScoped<IMediator>(_ => new MediadorPorFuncion(p => responder((ObtenerPersonasConCarteraQuery)p)));
        return Render<CabeceraGestorCae>();
    }

    private static PersonaConCartera Persona(string nombre, string rol = Roles.GestorCae, DateTime? hasta = null, string? avatar = null) =>
        new(Guid.NewGuid(), nombre, rol, hasta, avatar);

    private static CarterasDeOperacion Operacion(PersonaConCartera? principal, params PersonaConCartera[] apoyos) =>
        new(Guid.NewGuid(), Tenant, "Talleres Norte", principal, apoyos);

    [Fact]
    public void Pinta_al_principal_y_a_los_de_apoyo_con_la_fecha_de_fin_de_quien_la_tiene()
    {
        ObtenerPersonasConCarteraQuery? pedida = null;
        var cut = Renderizar(q =>
        {
            pedida = q;
            return [Operacion(
                Persona("Marta Ibarra", avatar: "buho-ambar"),
                Persona("Unai Zabala", hasta: new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc)),
                Persona("Ane Larrea"))];
        });

        pedida!.TenantId.Should().Be(Tenant, "se pregunta por el Tenant propietario activo, no por todos los que opera");

        cut.Find(".cabecera-gestor-cae-rotulo").TextContent.Trim().Should().Be("Gestor CAE");

        var principal = cut.Find("[data-gestor-cae='principal']");
        principal.QuerySelector("strong")!.TextContent.Trim().Should().Be("Marta Ibarra");
        principal.QuerySelector(".cabecera-gestor-cae-pastilla")!.TextContent.Trim().Should().Be("Principal");
        principal.QuerySelectorAll(".avatar-usuario-glifo").Should().ContainSingle("lleva el avatar que eligió");

        var apoyos = cut.FindAll("[data-gestor-cae='apoyo']");
        apoyos.Select(a => a.QuerySelector(".cabecera-gestor-cae-pastilla")!.TextContent.Trim())
            .Should().Equal(["Apoyo", "Apoyo hasta el 31/12/2026"], "por nombre: Ane Larrea, Unai Zabala");
        apoyos[0].TextContent.Should().Contain("Ane Larrea");
        apoyos[0].QuerySelector(".avatar-usuario")!.TextContent.Trim().Should().Be("AL", "sin avatar elegido, sus iniciales");

        cut.FindAll("button, a, input").Should().BeEmpty("es solo lectura: aquí no hay ninguna acción");
    }

    [Fact]
    public void Un_principal_que_gestiona_como_Coordinador_CAE_se_rotula_asi()
    {
        var cut = Renderizar(_ => [Operacion(Persona("Iker Sola", Roles.CoordinadorCae))]);

        cut.Find("[data-gestor-cae='principal'] .cabecera-gestor-cae-pastilla").TextContent.Trim()
            .Should().Be("Coordinador CAE principal");
    }

    /// <summary>Sin principal es un estado válido: se dice, y los de apoyo siguen saliendo.</summary>
    [Fact]
    public void Sin_principal_lo_dice_y_sigue_pintando_a_los_de_apoyo()
    {
        var cut = Renderizar(_ => [Operacion(null, Persona("Ane Larrea"))]);

        cut.Find("[data-gestor-cae='sin-principal']").TextContent.Trim().Should().Be("Sin principal");
        cut.FindAll("[data-gestor-cae='principal']").Should().BeEmpty();
        cut.FindAll("[data-gestor-cae='apoyo']").Should().ContainSingle();
    }

    /// <summary>
    /// La consulta sale vacía para quien no es una cuenta de gestión CAE del Operador CAE sobre
    /// este Tenant (un usuario del propio Tenant propietario, Soporte TALVEG): no se pinta nada,
    /// ni siquiera el rótulo.
    /// </summary>
    [Fact]
    public void Si_la_consulta_sale_vacia_no_pinta_nada()
    {
        var cut = Renderizar(_ => []);

        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Las_carteras_de_otro_Tenant_no_se_pintan_aqui()
    {
        var cut = Renderizar(_ => [new CarterasDeOperacion(Guid.NewGuid(), OtroTenant, "Otro", Persona("Marta Ibarra"), [])]);

        cut.Markup.Trim().Should().BeEmpty("la cabecera es del Tenant propietario activo");
    }

    [Fact]
    public void Sin_Tenant_activo_ni_pregunta_ni_pinta()
    {
        var preguntas = 0;
        var cut = Renderizar(_ => { preguntas++; return []; }, sinTenant: true);

        preguntas.Should().Be(0);
        cut.Markup.Trim().Should().BeEmpty();
    }

    /// <summary>Un fallo no es «nadie lo gestiona»: se dice, en vez de dejar la cabecera muda.</summary>
    [Fact]
    public void Si_la_consulta_falla_lo_dice_en_vez_de_callar()
    {
        var cut = Renderizar(_ => throw new InvalidOperationException("Fallo simulado."));

        cut.Find(".cabecera-gestor-cae [role=status]").TextContent.Trim().Should().Be("No pudimos cargarlo");
        cut.FindAll("[data-gestor-cae]").Should().BeEmpty();
    }
}

/// <summary>Mediador de un solo responder, para componentes que lanzan una o dos consultas.</summary>
internal sealed class MediadorPorFuncion(Func<object, object?> responder) : IMediator
{
    public List<object> Enviadas { get; } = [];

    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        Enviadas.Add(request);
        try
        {
            return Task.FromResult((TResponse)responder(request)!);
        }
        catch (Exception ex) when (ex is not InvalidCastException)
        {
            return Task.FromException<TResponse>(ex);
        }
    }

    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest
    {
        Enviadas.Add(request!);
        return Task.CompletedTask;
    }

    public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
        Task.FromResult(responder(request));

    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification => Task.CompletedTask;
}
