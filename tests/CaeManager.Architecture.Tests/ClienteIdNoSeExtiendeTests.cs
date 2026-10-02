using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Trinquete por ubicación de <c>ClienteId</c></b> — Fase 0 del plan de migración de los
/// restos del modelo <c>Cliente</c> a <c>Empresa</c> + <c>RelacionEmpresarial</c> (ADR-011) y S11
/// del análisis de causas raíz, ambos de 2026-10-02 (<c>Project-Hydra-Negocio/tecnico/</c>).
///
/// <para>
/// <b>Qué vigila.</b> La entidad <c>Cliente</c> ya no existe: lo que queda es una columna
/// <c>ClienteId</c> que es un <c>Empresa.Id</c> en posición «Cliente empresarial», sin ningún
/// instrumento que impida que se extienda. Dos instrumentos:
/// <list type="number">
/// <item><b>Modelo EF</b> (<see cref="El_modelo_EF_no_gana_propiedades_ni_columnas_ClienteId"/>): los
/// metadatos de <c>CaeManagerDbContext</c>, sin conexión. Ve la propiedad por nombre y por
/// <b>columna física</b>, así que una propiedad renombrada que siga mapeada a <c>ClienteId</c>
/// (opción (b) de la decisión D1 del plan) no se vuelve invisible.</item>
/// <item><b>Código</b> (<see cref="Las_ubicaciones_de_ClienteId_en_el_codigo_solo_decrecen"/>): cada
/// pareja <c>fichero :: identificador</c> con su recuento, sobre <c>.cs</c> (identificadores de
/// Roslyn), <c>.razor</c> (sin comentarios) y <c>.resx</c> neutral. La palabra se busca como
/// subcadena sin distinguir mayúsculas, para que <c>ClienteIds</c>, <c>TenantClienteId</c> o la
/// variable <c>clienteId</c> cuenten.</item>
/// </list>
/// Cada lista vive en <c>Congelados/</c> y obedece a <see cref="ListaCongelada"/>: un uso nuevo en
/// un fichero nuevo, o más usos en uno ya listado, ponen el test en rojo; menos usos también,
/// para que la lista baje con el código.
/// </para>
///
/// <para>
/// <b>Qué decide y qué no.</b> No dice que <c>ClienteId</c> esté mal: la decisión D1 (estado final
/// de <c>ClienteId</c>) es del propietario y está abierta. Congela el hecho para que no crezca
/// mientras se decide. Cuando D1 se resuelva, la lista baja (renombrado) o se cierra
/// (se declara el nombre canónico y la lista queda como inventario).
/// </para>
///
/// <para>
/// <b>Qué no ve</b> (declarado, no por inercia): <c>tests/</c> (decenas de ficheros lo usan, y son
/// consumidores que se reescriben al contraer), las migraciones, y el texto de un literal de
/// cadena en C# (<c>HasColumnName("ClienteId")</c>), que cubre el test del modelo. Una
/// propiedad shadow o una columna creada por una configuración sin propiedad CLR sí entra en el
/// test del modelo.
/// </para>
/// </summary>
public class ClienteIdNoSeExtiendeTests
{
    private const string Palabra = "ClienteId";

    // El suelo del escáner NO es la lista: es "el análisis ve el árbol entero". Si la enumeración
    // se rompiera, todas las líneas de la lista pasarían a OBSOLETA; este suelo lo dice con su nombre.
    private const int SueloDeFicherosAnalizados = 1000;

    [Fact]
    public void Las_ubicaciones_de_ClienteId_en_el_codigo_solo_decrecen()
    {
        var medido = FuentesDeSrc.UbicacionesDePalabras(FuentesDeSrc.Analisis, EsClienteId);

        var fallo = ListaCongelada.Verificar("ClienteId-ubicaciones", medido,
            "ClienteId es deuda terminológica y de modelo (Empresa.Id en posición Cliente empresarial, sin FK en varias " +
            "columnas; plan de migración de Cliente, Fase 0). Un uso NUEVO se evita usando el concepto canónico " +
            "(RelacionEmpresarial / posición cliente de la Relación Empresarial). Si el uso es inevitable y deliberado, " +
            "añade la línea a la lista en el mismo commit y di por qué en la PR. Si has RETIRADO usos, baja o borra su " +
            "línea. Para regenerar la medida: HYDRA_TRINQUETES_VOLCAR=<directorio> dotnet test … y copia el resultado " +
            "tras revisar el diff. Régimen: .cs = identificadores de Roslyn; .razor = texto sin comentarios; .resx = valores.");

        fallo.Should().BeNull();
    }

    [Fact]
    public void El_modelo_EF_no_gana_propiedades_ni_columnas_ClienteId()
    {
        var medido = PropiedadesYColumnasClienteId();

        var fallo = ListaCongelada.Verificar("ClienteId-modelo-ef", medido,
            "Una propiedad o columna *ClienteId nueva en el modelo EF extiende un resto del modelo Cliente sin " +
            "integridad referencial comprobada (plan de migración, R2). Modela la relación con la Relación Empresarial " +
            "o con la Empresa por su concepto canónico. Si es deliberado, añade la línea y justifícalo en la PR; si " +
            "retiras una, borra su línea. Formato: 'Entidad :: Propiedad -> columna = 1'.");

        fallo.Should().BeNull();
    }

    [Fact]
    public void El_modelo_EF_se_mide_de_verdad_no_esta_vacio()
    {
        // Control positivo del instrumento del modelo, independiente de la lista: el modelo
        // del DbContext tiene muchísimas entidades y propiedades, y entre ellas la Empresa. Si el
        // modelo llegara vacío (otro DbContext, otro constructor), la lista entera pasaría a OBSOLETA,
        // pero esto lo dice con su nombre.
        using var contexto = CrearContextoSinConexion();
        var modelo = contexto.GetService<IDesignTimeModel>().Model;

        modelo.GetEntityTypes().Count().Should().BeGreaterThan(50);
        modelo.GetEntityTypes().SelectMany(e => e.GetProperties()).Count().Should().BeGreaterThan(500);
    }

    [Fact]
    public void El_escaner_analiza_el_arbol_entero()
    {
        FuentesDeSrc.Analisis.Keys.Count(k => k.EndsWith(".cs", StringComparison.Ordinal))
            .Should().BeGreaterThan(SueloDeFicherosAnalizados);
        FuentesDeSrc.Analisis.Keys.Count(k => k.EndsWith(".razor", StringComparison.Ordinal)).Should().BeGreaterThan(100);
        FuentesDeSrc.Analisis.Keys.Count(k => k.EndsWith(".resx", StringComparison.Ordinal)).Should().BeGreaterThan(20);
        FuentesDeSrc.Analisis.Keys.Should().NotContain(k => k.Contains("/Migrations/") || k.Contains("/obj/") || k.Contains("/bin/"));
    }

    // ───────────── control positivo del detector, con fuentes sintéticas ─────────────

    [Theory]
    [InlineData("ClienteId", true)]
    [InlineData("clienteId", true)]
    [InlineData("TenantClienteId", true)]
    [InlineData("AmbitoRelacionClienteId", true)]
    [InlineData("ObtenerClienteIdsVisiblesAsync", true)]
    [InlineData("ClienteActivo", false)]
    [InlineData("ClienteNombre", false)]
    [InlineData("Cliente", false)]
    [InlineData("TitularId", false)]
    public void El_predicado_casa_con_el_identificador_legacy_y_con_nada_mas(string palabra, bool esperado) =>
        EsClienteId(palabra).Should().Be(esperado);

    [Fact]
    public void En_C_sharp_solo_cuenta_el_identificador_no_el_comentario_ni_el_literal()
    {
        const string fuente = """
            using System;
            // ClienteId en un comentario
            /// <summary>ClienteId en un doc-comment</summary>
            public class A
            {
                public Guid ClienteId { get; set; }          // identificador: cuenta (1)
                public string S = "ClienteId en un literal"; // literal: no cuenta
                public void M(Guid clienteId) { var otro = nameof(ClienteId); } // clienteId (1) y nameof(ClienteId) (1 más)
            }
            """;

        var palabras = FuentesDeSrc.AnalizarCSharp(fuente).Palabras;

        palabras["ClienteId"].Should().Be(2, "la declaración de la propiedad y el argumento de nameof; el comentario, el doc-comment y el literal no");
        palabras["clienteId"].Should().Be(1);
    }

    [Fact]
    public void En_Razor_el_comentario_no_cuenta_pero_el_marcado_visible_y_el_codigo_si()
    {
        const string fuente = """
            @page "/x/{ClienteId:guid}"
            @* ClienteId en un comentario Razor *@
            <!-- ClienteId en un comentario HTML -->
            <p>ClienteId visible</p>
            @code {
                // ClienteId en un comentario de C#
                [Parameter] public Guid ClienteId { get; set; }
            }
            """;

        FuentesDeSrc.AnalizarRazor(fuente).Palabras["ClienteId"].Should().Be(3, "la ruta, el texto visible y el parámetro");
    }

    [Fact]
    public void En_un_resx_cuenta_el_valor_pero_no_la_clave_ni_el_comentario()
    {
        const string resx = """
            <?xml version="1.0" encoding="utf-8"?>
            <root>
              <data name="ClienteIdClave" xml:space="preserve"><value>valor sin la palabra</value><comment>ClienteId</comment></data>
              <data name="Otra" xml:space="preserve"><value>mira ClienteId aquí</value></data>
            </root>
            """;

        FuentesDeSrc.AnalizarResx(resx).Palabras.Should().ContainKey("ClienteId").WhoseValue.Should().Be(1);
    }

    private static bool EsClienteId(string palabra) =>
        palabra.Contains(Palabra, StringComparison.OrdinalIgnoreCase);

    private static Dictionary<Ubicacion, int> PropiedadesYColumnasClienteId()
    {
        using var contexto = CrearContextoSinConexion();
        var modelo = contexto.GetService<IDesignTimeModel>().Model;
        var resultado = new Dictionary<Ubicacion, int>();

        foreach (var entidad in modelo.GetEntityTypes())
        {
            foreach (var propiedad in entidad.GetProperties())
            {
                var columna = propiedad.GetColumnName();
                if (!EsClienteId(propiedad.Name) && !(columna is not null && EsClienteId(columna)))
                    continue;

                var ubicacion = new Ubicacion(entidad.DisplayName(), $"{propiedad.Name} -> {columna ?? "(sin columna)"}");
                resultado[ubicacion] = resultado.GetValueOrDefault(ubicacion) + 1;
            }
        }

        return resultado;
    }

    private static CaeManagerDbContext CrearContextoSinConexion()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = Guid.NewGuid() };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql("Host=localhost;Database=solo-para-construir-el-modelo;Username=x;Password=x")
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
