using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Auditoria.Queries;
using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores.Commands.RestaurarTrabajador;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Pantalla de Auditoría (Gen 2): cabecera integrable, exportación con el
/// filtro vigente, consulta enviada con sus parámetros, carga vigente frente a
/// respuestas fuera de orden y la columna de acciones.
///
/// <para>
/// <b>Lo que esto SÍ observa:</b> la consulta y los comandos que llegan al
/// mediador —con sus parámetros— y la URL resultante, además del marcado. El
/// doble del mediador APLICA el filtro de la consulta que recibe: uno que lo
/// ignorase dejaría en verde una pantalla que no lo envía.
/// </para>
///
/// <para>
/// <b>Lo que NO observa:</b> que el servidor filtre de verdad ni qué registra
/// <c>AuditoriaInterceptor</c> — eso vive en Application/Infrastructure.
/// </para>
/// </summary>
public class AuditoriaPantallaTests : BunitContext
{
    private sealed class MediatorAuditoria : IMediator
    {
        public List<ObtenerAuditoriaQuery> Consultas { get; } = [];
        public List<object> Comandos { get; } = [];
        public List<RegistroAuditoriaListaDto> Filas { get; } = [];

        /// <summary>Total que declara la respuesta; sin valor, el número de filas que casan.</summary>
        public int? TotalDeclarado { get; set; }

        /// <summary>
        /// Sustituye la respuesta por defecto — p. ej. por una tarea que el
        /// test resuelve cuando quiere, para ordenar respuestas a su antojo.
        /// </summary>
        public Func<ObtenerAuditoriaQuery, Task<ResultadoPaginado<RegistroAuditoriaListaDto>>>? Responder { get; set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            switch (request)
            {
                case ObtenerAuditoriaQuery consulta:
                    Consultas.Add(consulta);
                    return Convertir<TResponse>((Responder ?? ResponderAplicandoFiltro)(consulta));
                case ICommand comando:
                    Comandos.Add(comando);
                    return Task.FromResult((TResponse)(object)Result.Exito());
                default:
                    throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.");
            }
        }

        private Task<ResultadoPaginado<RegistroAuditoriaListaDto>> ResponderAplicandoFiltro(ObtenerAuditoriaQuery consulta)
        {
            var casan = Filas.Where(f => consulta.EntidadTipo is null || f.EntidadTipo == consulta.EntidadTipo).ToList();
            return Task.FromResult(new ResultadoPaginado<RegistroAuditoriaListaDto>(
                casan, TotalDeclarado ?? casan.Count, consulta.Pagina, consulta.TamanoPagina));
        }

        private static async Task<TResponse> Convertir<TResponse>(Task<ResultadoPaginado<RegistroAuditoriaListaDto>> tarea) =>
            (TResponse)(object)await tarea;

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

    private readonly MediatorAuditoria _mediador = new();
    private readonly Dictionary<string, ApplicationUser> _usuarios = [];

    private IRenderedComponent<Features.Auditoria.Pages.Auditoria> Renderizar(string? entidad = null, bool integrada = false)
    {
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<PuertaAccesoDatos>();
        Services.AddScoped<ToastService>();
        Services.AddScoped(_ => new UserManager<ApplicationUser>(
            new AlmacenUsuarios(_usuarios), null!, null!, null!, null!, null!, null!, null!, null!));

        // [SupplyParameterFromQuery]: se llega navegando, igual que en el producto.
        Navegacion.NavigateTo(entidad is null ? "auditoria" : "auditoria?entidad=" + Uri.EscapeDataString(entidad));

        return Render<Features.Auditoria.Pages.Auditoria>(parametros => parametros
            .Add(p => p.IntegradaEnConfiguracion, integrada));
    }

    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();

    private static RegistroAuditoriaListaDto Fila(
        string entidad, string accion, Guid? usuarioId = null, bool puedeRestaurar = false, bool archivoAnterior = false) =>
        new(Guid.NewGuid(), entidad, Guid.NewGuid(), accion, usuarioId, DateTime.UtcNow, puedeRestaurar, archivoAnterior);

    private static IElement FilaDe(IRenderedComponent<Features.Auditoria.Pages.Auditoria> cut, string entidad) =>
        cut.FindAll("table.tabla-datos tbody tr").Single(tr => tr.QuerySelectorAll("td")[1].TextContent == entidad);

    private static IElement Boton(IRenderedComponent<Features.Auditoria.Pages.Auditoria> cut, string texto) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim().Contains(texto, StringComparison.Ordinal));

    [Fact]
    public void Con_ruta_propia_el_titulo_es_h1_con_la_entradilla_de_la_pantalla()
    {
        var cut = Renderizar();

        cut.Find(".contenedor-pagina header.cabecera-pagina h1").TextContent.Should().Be("Auditoría");
        cut.Find(".cabecera-pagina-descripcion").TextContent.Should().Contain("quién lo hizo y cuándo");
    }

    [Fact]
    public void Embebida_en_Configuracion_el_titulo_es_h2_y_no_hay_ningun_h1()
    {
        var cut = Renderizar(integrada: true);

        cut.Find(".contenido-panel-configuracion h2.titulo-panel-configuracion").TextContent.Should().Be("Auditoría");
        cut.FindAll("h1").Should().BeEmpty("el hub de Configuración ya pone el h1 de la página");
    }

    [Fact]
    public void La_exportacion_esta_en_la_cabecera_y_arrastra_el_filtro_de_la_url()
    {
        var cut = Renderizar(entidad: "Documento");

        cut.Find(".cabecera-pagina .acciones-cabecera a.enlace-exportar").GetAttribute("href")
            .Should().Be("/auditoria/exportar.xlsx?entidad=Documento");
    }

    [Fact]
    public async Task Cambiar_el_filtro_consulta_ese_tipo_desde_la_pagina_1_y_lo_lleva_a_la_url()
    {
        _mediador.Filas.AddRange([Fila("Trabajador", "Creado"), Fila("Documento", "Creado")]);
        _mediador.TotalDeclarado = 45;
        var cut = Renderizar();

        await Boton(cut, "Siguiente").ClickAsync(new MouseEventArgs());
        _mediador.Consultas[^1].Pagina.Should().Be(2, "es el punto de partida: el filtro debe devolver a la página 1");

        await cut.Find("select").ChangeAsync(new ChangeEventArgs { Value = "Trabajador" });

        _mediador.Consultas[^1].Should().Be(new ObtenerAuditoriaQuery("Trabajador", UsuarioId: null, Pagina: 1, TamanoPagina: 30));
        Navegacion.Uri.Should().Contain("entidad=Trabajador");
        cut.FindAll("table.tabla-datos tbody tr").Should().ContainSingle()
            .Which.TextContent.Should().Contain("Trabajador");
    }

    [Fact]
    public async Task Paginar_pide_la_pagina_siguiente_sin_perder_el_filtro()
    {
        _mediador.Filas.Add(Fila("Documento", "Creado"));
        _mediador.TotalDeclarado = 45;
        var cut = Renderizar(entidad: "Documento");

        await Boton(cut, "Siguiente").ClickAsync(new MouseEventArgs());

        _mediador.Consultas[^1].Should().Be(new ObtenerAuditoriaQuery("Documento", UsuarioId: null, Pagina: 2, TamanoPagina: 30));
    }

    /// <summary>
    /// La carga inicial (sin filtro) sigue en vuelo cuando se filtra por
    /// «Documento»; la respuesta nueva llega primero y la vieja después. Si la
    /// vieja se pintara, la tabla enseñaría filas de «Trabajador» bajo un
    /// desplegable y un enlace de exportar que dicen «Documento».
    ///
    /// <para>
    /// Cada respuesta se resuelve DENTRO del dispatcher del renderer: la
    /// continuación de la página corre en línea y, al volver el
    /// <c>InvokeAsync</c>, ya ha escrito (o descartado) su estado — es la
    /// barrera que evita un verde vacío en la aserción de ausencia.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Una_respuesta_tardia_de_la_carga_anterior_no_pisa_la_del_filtro_vigente()
    {
        var pendientes = new List<TaskCompletionSource<ResultadoPaginado<RegistroAuditoriaListaDto>>>();
        _mediador.Responder = _ =>
        {
            var pendiente = new TaskCompletionSource<ResultadoPaginado<RegistroAuditoriaListaDto>>();
            pendientes.Add(pendiente);
            return pendiente.Task;
        };
        var cut = Renderizar();
        pendientes.Should().HaveCount(1, "la carga inicial queda en vuelo");

        var cambio = cut.Find("select").ChangeAsync(new ChangeEventArgs { Value = "Documento" });
        pendientes.Should().HaveCount(2);
        _mediador.Consultas[^1].EntidadTipo.Should().Be("Documento");

        await cut.InvokeAsync(() => pendientes[1].SetResult(
            new ResultadoPaginado<RegistroAuditoriaListaDto>([Fila("Documento", "Creado")], 1, 1, 30)));
        await cambio;
        await cut.InvokeAsync(() => pendientes[0].SetResult(
            new ResultadoPaginado<RegistroAuditoriaListaDto>([Fila("Trabajador", "Eliminado")], 1, 1, 30)));

        var filas = cut.FindAll("table.tabla-datos tbody tr");
        filas.Should().ContainSingle("solo cuenta la respuesta de la carga vigente");
        filas[0].TextContent.Should().Contain("Documento").And.NotContain("Trabajador");
        cut.FindAll(".estado-vacio").Should().BeEmpty();
    }

    [Fact]
    public async Task Quitar_el_filtro_limpia_tambien_la_url_y_vuelve_a_consultar_sin_filtro()
    {
        var cut = Renderizar(entidad: "Documento");
        Navegacion.Uri.Should().Contain("entidad=Documento", "es el punto de partida");

        await Boton(cut, "Quitar el filtro").ClickAsync(new MouseEventArgs());

        Navegacion.Uri.Should().NotContain("entidad=");
        _mediador.Consultas[^1].EntidadTipo.Should().BeNull();
    }

    [Fact]
    public void La_columna_de_acciones_explica_la_baja_logica_y_nunca_ofrece_dos_acciones()
    {
        _mediador.Filas.AddRange([
            Fila("Trabajador", "Modificado", puedeRestaurar: true),
            Fila("Documento", "Modificado", puedeRestaurar: true, archivoAnterior: true),
            Fila("Empresa", "Modificado"),
            Fila("Cliente", "Creado")]);
        var cut = Renderizar();

        cut.FindAll("table.tabla-datos thead th")[^1].TextContent.Should().Be("Acciones");

        var baja = FilaDe(cut, "Trabajador");
        baja.QuerySelector(".detalle-baja-logica")!.TextContent.Should().Contain("eliminado lógicamente");
        baja.QuerySelectorAll("button").Should().ContainSingle().Which.TextContent.Should().Contain("Restaurar");

        var conArchivo = FilaDe(cut, "Documento");
        conArchivo.QuerySelector("a")!.TextContent.Should().Be("Ver archivo anterior");
        conArchivo.QuerySelectorAll("button").Should().BeEmpty("si hay archivo anterior, nunca aparece Restaurar");

        var modificacion = FilaDe(cut, "Empresa");
        modificacion.QuerySelector(".detalle-baja-logica").Should().BeNull("un Modificado sin baja lógica no es una baja");
        modificacion.QuerySelectorAll("td")[^1].TextContent.Trim().Should().Be("—");
        FilaDe(cut, "Cliente").QuerySelector(".detalle-baja-logica").Should().BeNull();
    }

    [Fact]
    public async Task Restaurar_envia_el_comando_de_su_entidad_y_recarga_la_lista()
    {
        var baja = Fila("Trabajador", "Modificado", puedeRestaurar: true);
        _mediador.Filas.Add(baja);
        var cut = Renderizar();
        _mediador.Consultas.Should().HaveCount(1);

        await Boton(cut, "Restaurar").ClickAsync(new MouseEventArgs());

        _mediador.Comandos.Should().ContainSingle().Which.Should().Be(new RestaurarTrabajadorCommand(baja.EntidadId));
        _mediador.Consultas.Should().HaveCount(2, "tras restaurar se recarga la página vigente");
    }

    [Fact]
    public void Un_filtro_de_la_url_fuera_del_desplegable_se_ofrece_seleccionado()
    {
        var cut = Renderizar(entidad: "LineaWhatsApp");

        cut.FindAll("select option[value='LineaWhatsApp']").Should().ContainSingle();
        cut.Find("select").GetAttribute("value").Should().Be("LineaWhatsApp");
        _mediador.Consultas[^1].EntidadTipo.Should().Be("LineaWhatsApp");
    }

    [Fact]
    public void Un_filtro_de_las_entidades_principales_no_se_duplica_en_el_desplegable()
    {
        var cut = Renderizar(entidad: "Documento");

        cut.FindAll("select option[value='Documento']").Should().ContainSingle();
    }

    [Fact]
    public void El_autor_se_resuelve_por_identity_y_el_que_ya_no_existe_se_atenua()
    {
        var conocido = Guid.NewGuid();
        var desaparecido = Guid.NewGuid();
        _usuarios[conocido.ToString()] = new ApplicationUser { Id = conocido, NombreCompleto = "Marta Ruiz" };
        _mediador.Filas.AddRange([
            Fila("Trabajador", "Creado", usuarioId: conocido),
            Fila("Empresa", "Creado", usuarioId: desaparecido),
            Fila("Centro", "Creado", usuarioId: null)]);
        var cut = Renderizar();

        var celdaConocido = FilaDe(cut, "Trabajador").QuerySelectorAll("td")[3];
        celdaConocido.TextContent.Should().Be("Marta Ruiz");
        celdaConocido.QuerySelector(".usuario-no-resuelto").Should().BeNull();

        var celdaDesaparecido = FilaDe(cut, "Empresa").QuerySelectorAll("td")[3];
        celdaDesaparecido.TextContent.Should().Be("(usuario eliminado)");
        celdaDesaparecido.QuerySelector(".usuario-no-resuelto").Should().NotBeNull();

        FilaDe(cut, "Centro").QuerySelectorAll("td")[3].TextContent.Should().Be("Sistema");
    }
}
