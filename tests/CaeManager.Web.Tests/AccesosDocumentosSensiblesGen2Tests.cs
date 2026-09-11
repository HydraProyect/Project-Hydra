using System.Reflection;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Auditoria.Queries;
using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Auditoria.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Pantalla de accesos a documentos sensibles contra su mockup Gen 2
/// («Accesos Documentos Sensibles TALVEG.dc.html»).
///
/// <para>
/// <b>Lo que esto SÍ observa:</b> el atributo de autorización de la página,
/// el marcado (cabecera, avisos, filas, tonos, estados), la consulta que llega
/// al mediador —con su página, su tamaño y su token— y que una respuesta
/// superada o tardía no pinte nada. El doble del mediador APLICA la
/// paginación que recibe, como <c>ObtenerAccesosDocumentosSensiblesQueryHandler</c>:
/// uno que la ignorase dejaría en verde una pantalla que no la envía.
/// </para>
///
/// <para>
/// <b>Lo que NO observa:</b> que la política se evalúe de verdad (eso es
/// ASP.NET Core con la política de Program.cs), qué se registra ni la RLS de
/// la tabla.
/// </para>
/// </summary>
public class AccesosDocumentosSensiblesGen2Tests : BunitContext
{
    private sealed class MediatorAccesos : IMediator
    {
        public List<AccesoDocumentoSensibleDto> Almacen { get; } = [];
        public List<ObtenerAccesosDocumentosSensiblesQuery> Consultas { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public bool Fallar { get; set; }

        /// <summary>Si devuelve una tarea para la consulta, esa es la respuesta: permite retenerla y resolverla fuera de orden.</summary>
        public Func<ObtenerAccesosDocumentosSensiblesQuery, Task<ResultadoPaginado<AccesoDocumentoSensibleDto>>?>? Retener { get; set; }

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is not ObtenerAccesosDocumentosSensiblesQuery consulta)
                throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.");

            Consultas.Add(consulta);
            Tokens.Add(cancellationToken);

            if (Retener?.Invoke(consulta) is { } retenida)
                return (TResponse)(object)await retenida;

            if (Fallar)
                throw new InvalidOperationException("Fallo simulado de la consulta del rastro.");

            return (TResponse)(object)Paginar(consulta);
        }

        /// <summary>Mismo orden de pasos que el handler: más reciente primero y paginado.</summary>
        public ResultadoPaginado<AccesoDocumentoSensibleDto> Paginar(ObtenerAccesosDocumentosSensiblesQuery consulta)
        {
            var pagina = Almacen
                .OrderByDescending(a => a.OcurridoEnUtc)
                .Skip((consulta.Pagina - 1) * consulta.TamanoPagina)
                .Take(consulta.TamanoPagina)
                .ToList();
            return new ResultadoPaginado<AccesoDocumentoSensibleDto>(pagina, Almacen.Count, consulta.Pagina, consulta.TamanoPagina);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    /// <summary>Solo responde a <c>FindByIdAsync</c>, que es lo único que la página pide.</summary>
    private sealed class AlmacenUsuarios(Dictionary<string, ApplicationUser> usuarios) : IUserStore<ApplicationUser>
    {
        private static Exception NoPrevisto() => new NotSupportedException("La página solo busca usuarios por Id.");

        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) =>
            Task.FromResult(usuarios.GetValueOrDefault(userId));

        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public void Dispose() { }
    }

    private readonly MediatorAccesos _mediador = new();
    private readonly Dictionary<string, ApplicationUser> _usuarios = [];

    private void Registrar()
    {
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<PuertaAccesoDatos>();
        Services.AddScoped(_ => new UserManager<ApplicationUser>(
            new AlmacenUsuarios(_usuarios), null!, null!, null!, null!, null!, null!, null!, null!));
    }

    private IRenderedComponent<AccesosDocumentosSensibles> Renderizar(bool integrada = false)
    {
        Registrar();
        var cut = Render<AccesosDocumentosSensibles>(p => p.Add(x => x.IntegradaEnConfiguracion, integrada));
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("aria-busy=\"true\""));
        return cut;
    }

    private static readonly DateTime Base = new(2026, 9, 5, 15, 44, 37, DateTimeKind.Utc);

    private static AccesoDocumentoSensibleDto Acceso(
        Guid? usuarioId = null,
        SensibilidadDocumental sensibilidad = SensibilidadDocumental.CategoriaEspecialSalud,
        TipoAccesoDocumentoSensible tipo = TipoAccesoDocumentoSensible.Apertura,
        TipoViaAccesoAuditoria via = TipoViaAccesoAuditoria.Normal,
        DateTime? ocurrido = null,
        Guid? documentoId = null) =>
        new(Guid.NewGuid(), documentoId ?? Guid.NewGuid(), sensibilidad, tipo, usuarioId, ocurrido ?? Base, via,
            via == TipoViaAccesoAuditoria.SesionPrivilegiada);

    private Guid Usuario(string? nombre, string? email)
    {
        // IdentityUser<Guid> no asigna Id solo: sin esto todos serían Guid.Empty.
        var usuario = new ApplicationUser { Id = Guid.NewGuid(), NombreCompleto = nombre!, Email = email };
        _usuarios[usuario.Id.ToString()] = usuario;
        return usuario.Id;
    }

    /// <summary>Texto visible con los espacios colapsados, como lo lee el navegador.</summary>
    private static string Texto(IElement elemento) =>
        string.Join(' ', elemento.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static List<IElement> Filas(IRenderedComponent<AccesosDocumentosSensibles> cut) =>
        cut.FindAll(".accesos-sensibles-tabla tbody tr").ToList();

    private static string Celda(IElement fila, int columna) => Texto(fila.QuerySelectorAll("td")[columna]);

    /// <summary>Tantos accesos como se pidan, uno por minuto hacia atrás: el primero es el más reciente.</summary>
    private void Sembrar(int cantidad)
    {
        for (var i = 0; i < cantidad; i++)
            _mediador.Almacen.Add(Acceso(ocurrido: Base.AddMinutes(-i), documentoId: new Guid(i + 1, 0, 0, new byte[8])));
    }

    private static string IdDocumento(int n) => new Guid(n, 0, 0, new byte[8]).ToString();

    // ------------------------------------------------------------ Autorización

    /// <summary>
    /// La página conserva su política: la del permiso granular, no un rol.
    /// Un <c>[Authorize(Roles = Administrador)]</c> dejaría entrar a cualquier
    /// Administrador sin el permiso — justo lo que la política existe para
    /// impedir.
    /// </summary>
    [Fact]
    public void La_pagina_conserva_la_politica_del_permiso_granular_y_no_una_regla_de_rol()
    {
        var autorizacion = typeof(AccesosDocumentosSensibles).GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Should().ContainSingle().Which;

        autorizacion.Policy.Should().Be(Policies.ConsultarAccesoDocumentosSensibles);
        autorizacion.Roles.Should().BeNull("la política ya exige el rol Administrador y además el permiso");
    }

    // --------------------------------------------------------- Cabecera y avisos

    [Fact]
    public void La_cabecera_es_la_Gen_2_con_su_entradilla_y_los_dos_avisos_del_mockup()
    {
        var cut = Renderizar();

        var cabecera = cut.Find("header.cabecera-pagina");
        Texto(cabecera.QuerySelector("h1.titulo-pagina")!).Should().Be("Accesos a documentos sensibles");
        Texto(cabecera.QuerySelector(".cabecera-pagina-descripcion")!).Should()
            .Contain("abre o descarga un documento sensible").And.Contain("versiones anteriores");

        Texto(cut.Find(".accesos-sensibles-alcance strong")).Should().Be("Qué NO se registra, a propósito:");
        Texto(cut.Find(".accesos-sensibles-alcance")).Should()
            .Contain("adjuntos de comunicaciones").And.Contain("firmas y sellos guardados");

        Texto(cut.Find(".accesos-sensibles-puerta-titulo")).Should().Be("La puerta más estrecha del producto");
        Texto(cut.Find(".accesos-sensibles-puerta p")).Should()
            .Contain("otro Administrador").And.Contain("Dirección CAE no puede concederlo ni recibirlo")
            .And.Contain("no aparecen en la pantalla de Auditoría general");
    }

    /// <summary>Se conserva la integración: dentro del hub el título baja a h2 para no meter un segundo h1.</summary>
    [Fact]
    public void Integrada_en_configuracion_el_titulo_es_un_h2()
    {
        var cut = Renderizar(integrada: true);

        cut.FindAll("h1").Should().BeEmpty();
        Texto(cut.Find("header.cabecera-pagina h2.titulo-panel-configuracion")).Should().Be("Accesos a documentos sensibles");
    }

    // -------------------------------------------------------------------- Filas

    [Fact]
    public void Cuando_se_pinta_con_el_formato_de_la_app_y_sin_segundos()
    {
        _mediador.Almacen.Add(Acceso());
        var cut = Renderizar();

        var cuando = Celda(Filas(cut).Single(), 0);
        cuando.Should().Be(Base.ToLocalTime().ToString("dd/MM/yyyy HH:mm"));
        cuando.Should().NotContain(":37", "antes se pintaba ToLocalTime() a pelo, con segundos");
    }

    [Fact]
    public void Quien_es_el_nombre_o_el_correo_y_se_atenua_si_el_usuario_ya_no_existe_o_Sistema_si_fue_automatico()
    {
        var conNombre = Usuario("Marta Ruiz", "marta@refrielectric.es");
        var soloCorreo = Usuario(null, "admin.arcos@arcosspa.es");
        var eliminado = Guid.NewGuid();
        _mediador.Almacen.AddRange(
        [
            Acceso(conNombre, ocurrido: Base),
            Acceso(soloCorreo, ocurrido: Base.AddMinutes(-1)),
            Acceso(eliminado, ocurrido: Base.AddMinutes(-2)),
            Acceso(null, ocurrido: Base.AddMinutes(-3))
        ]);
        var cut = Renderizar();

        var quien = Filas(cut).Select(f => f.QuerySelectorAll("td")[1].QuerySelector("span")!).ToList();
        quien.Select(Texto).Should().Equal(["Marta Ruiz", "admin.arcos@arcosspa.es", "(usuario eliminado)", "Sistema"]);
        quien.Select(q => q.ClassList.Contains("accesos-sensibles-quien-no-resuelto"))
            .Should().Equal([false, false, true, false], "solo el autor que Identity no encuentra se atenúa");
    }

    [Fact]
    public void La_categoria_lleva_el_tono_del_mockup_salud_en_rojo_y_datos_personales_en_ambar()
    {
        _mediador.Almacen.AddRange(
        [
            Acceso(sensibilidad: SensibilidadDocumental.CategoriaEspecialSalud, ocurrido: Base),
            Acceso(sensibilidad: SensibilidadDocumental.DatosPersonales, ocurrido: Base.AddMinutes(-1)),
            Acceso(sensibilidad: SensibilidadDocumental.SinDatosPersonales, ocurrido: Base.AddMinutes(-2))
        ]);
        var cut = Renderizar();

        var badges = Filas(cut).Select(f => f.QuerySelectorAll("td")[3].QuerySelector(".badge")!).ToList();
        badges.Select(Texto).Should().Equal(["Salud (categoría especial)", "Datos personales", "Sin datos personales"]);
        badges.Select(b => b.ClassList.Contains("badge-peligro")).Should().Equal([true, false, false]);
        badges.Select(b => b.ClassList.Contains("badge-advertencia")).Should().Equal([false, true, false]);
    }

    /// <summary>«Sesión privilegiada» es el único contexto en ámbar; delegada y normal van en neutro.</summary>
    [Fact]
    public void El_contexto_pinta_la_sesion_privilegiada_en_ambar_y_el_resto_en_neutro()
    {
        _mediador.Almacen.AddRange(
        [
            Acceso(via: TipoViaAccesoAuditoria.SesionPrivilegiada, ocurrido: Base),
            Acceso(via: TipoViaAccesoAuditoria.OperacionDelegada, ocurrido: Base.AddMinutes(-1)),
            Acceso(via: TipoViaAccesoAuditoria.Normal, ocurrido: Base.AddMinutes(-2))
        ]);
        var cut = Renderizar();

        var badges = Filas(cut).Select(f => f.QuerySelectorAll("td")[5].QuerySelector(".badge")!).ToList();
        badges.Select(Texto).Should().Equal(["Sesión privilegiada", "Operación delegada", "Normal"]);
        badges.Select(b => b.ClassList.Contains("badge-advertencia")).Should().Equal([true, false, false]);
        badges.Skip(1).Should().OnlyContain(b => b.ClassList.Contains("badge-neutro"));
    }

    /// <summary>
    /// El documento sigue siendo su identificador (el título no está en la
    /// consulta) y la fila no ofrece nada que hacer: ni enlace ni botón.
    /// </summary>
    [Fact]
    public void La_fila_dice_el_tipo_de_acceso_y_el_identificador_del_documento_sin_ninguna_accion()
    {
        var documento = Guid.NewGuid();
        _mediador.Almacen.AddRange(
        [
            Acceso(tipo: TipoAccesoDocumentoSensible.Apertura, ocurrido: Base, documentoId: documento),
            Acceso(tipo: TipoAccesoDocumentoSensible.VersionAnterior, ocurrido: Base.AddMinutes(-1))
        ]);
        var cut = Renderizar();

        var filas = Filas(cut);
        filas.Select(f => Celda(f, 4)).Should().Equal(["Apertura", "Versión anterior"]);
        Celda(filas[0], 2).Should().Be(documento.ToString());
        cut.FindAll(".accesos-sensibles-tabla tbody a, .accesos-sensibles-tabla tbody button")
            .Should().BeEmpty("es una pantalla de solo lectura");
    }

    // ------------------------------------------------------------------ Estados

    [Fact]
    public void Sin_ningun_acceso_lo_dice_con_el_estado_vacio_y_sin_tabla()
    {
        var cut = Renderizar();

        Texto(cut.Find(".estado-vacio h3")).Should().Be("Sin accesos registrados todavía");
        cut.FindAll(".accesos-sensibles-tabla").Should().BeEmpty();
    }

    [Fact]
    public async Task Si_la_consulta_falla_lo_dice_y_Reintentar_pinta_lo_que_llega()
    {
        _mediador.Fallar = true;
        _mediador.Almacen.Add(Acceso());
        var cut = Renderizar();
        cut.Markup.Should().Contain("No pudimos cargar el rastro");
        cut.FindAll(".accesos-sensibles-tabla").Should().BeEmpty();

        _mediador.Fallar = false;
        await cut.Find(".estado-vacio button").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => Filas(cut).Should().ContainSingle());
        cut.Markup.Should().NotContain("No pudimos cargar el rastro");
    }

    // ---------------------------------------------------------------- Paginación

    [Fact]
    public void El_selector_de_tamano_ofrece_30_50_y_100_y_parte_de_30_como_la_consulta()
    {
        Sembrar(3);
        var cut = Renderizar();

        _mediador.Consultas.Single().TamanoPagina.Should().Be(30);
        var select = cut.Find(".paginador-tamano-select");
        select.QuerySelectorAll("option").Select(Texto).Should().Equal(["30", "50", "100"]);
        select.GetAttribute("value").Should().Be("30", "el selector dice el tamaño con el que de verdad se consulta");
    }

    [Fact]
    public async Task Siguiente_pide_la_pagina_2_y_pinta_sus_filas()
    {
        Sembrar(35);
        var cut = Renderizar();
        Filas(cut).Should().HaveCount(30);

        await cut.FindAll(".paginador button").Single(b => Texto(b) == "Siguiente").ClickAsync(new MouseEventArgs());

        _mediador.Consultas.Last().Pagina.Should().Be(2);
        cut.WaitForAssertion(() => Filas(cut).Select(f => Celda(f, 2)).Should().Equal(Enumerable.Range(31, 5).Select(IdDocumento)));
    }

    [Fact]
    public async Task Cambiar_el_tamano_desde_la_pagina_2_vuelve_a_la_pagina_1()
    {
        Sembrar(35);
        var cut = Renderizar();
        await cut.FindAll(".paginador button").Single(b => Texto(b) == "Siguiente").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Filas(cut).Should().HaveCount(5));

        await cut.Find(".paginador-tamano-select").ChangeAsync(new ChangeEventArgs { Value = "50" });

        var consulta = _mediador.Consultas.Last();
        consulta.Pagina.Should().Be(1);
        consulta.TamanoPagina.Should().Be(50);
        cut.WaitForAssertion(() => Filas(cut).Should().HaveCount(35));
    }

    // ------------------------------------------------------------------ Carreras

    /// <summary>
    /// Dos cambios de página seguidos (dos clics que llegan antes del
    /// repintado): la página 2 tarda y la 3 responde en seguida. Cuando la 2
    /// llega, no puede pisar las filas de la 3, que es la pregunta vigente.
    /// </summary>
    [Fact]
    public async Task Una_respuesta_lenta_de_una_pagina_superada_no_pisa_las_filas_de_la_vigente()
    {
        Sembrar(70);
        var lenta = new TaskCompletionSource<ResultadoPaginado<AccesoDocumentoSensibleDto>>();
        _mediador.Retener = q => q.Pagina == 2 ? lenta.Task : null;
        var cut = Renderizar();
        var cambiarPagina = cut.FindComponent<PaginadorSimple>().Instance.PaginaActualChanged;

        // Sin await: su manejador espera la respuesta retenida. Se espera al final.
        var aLaDos = cut.InvokeAsync(() => cambiarPagina.InvokeAsync(2));
        await cut.InvokeAsync(() => cambiarPagina.InvokeAsync(3));
        cut.WaitForAssertion(() => Filas(cut).Select(f => Celda(f, 2)).Should().Equal(Enumerable.Range(61, 10).Select(IdDocumento)));

        await cut.InvokeAsync(() => lenta.SetResult(_mediador.Paginar(new ObtenerAccesosDocumentosSensiblesQuery(2, 30))));
        await aLaDos;
        cut.Render();

        Filas(cut).Select(f => Celda(f, 2)).Should().Equal(Enumerable.Range(61, 10).Select(IdDocumento),
            "la respuesta de la página 2 llegó tarde: la pregunta vigente es la página 3");
        Texto(cut.Find(".paginador-texto")).Should().StartWith("Página 3 de 3");
    }

    /// <summary>
    /// Salir de la página cancela la consulta en curso, y su respuesta tardía
    /// ya no toca un componente retirado. Que el token quede cancelado
    /// demuestra además que el Dispose se ejecutó de verdad.
    /// </summary>
    [Fact]
    public async Task Salir_de_la_pagina_cancela_la_consulta_en_curso()
    {
        var respuesta = new TaskCompletionSource<ResultadoPaginado<AccesoDocumentoSensibleDto>>();
        _mediador.Retener = _ => respuesta.Task;
        Registrar();
        Render<AccesosDocumentosSensibles>();

        var token = _mediador.Tokens.Single();
        token.CanBeCanceled.Should().BeTrue("la consulta tiene que llevar el token del ciclo de la página");
        token.IsCancellationRequested.Should().BeFalse();

        await DisposeComponentsAsync();

        token.IsCancellationRequested.Should().BeTrue("salir de la página cancela la consulta en curso");
        var llegaTarde = () => respuesta.SetResult(_mediador.Paginar(new ObtenerAccesosDocumentosSensiblesQuery()));
        llegaTarde.Should().NotThrow("la respuesta tardía no repinta un componente retirado");
    }
}
