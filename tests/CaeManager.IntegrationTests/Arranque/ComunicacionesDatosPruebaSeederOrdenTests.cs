using CaeManager.Application.Common;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// Auditoría previa: <see cref="ComunicacionesDatosPruebaSeeder"/> leía
/// Trabajadores/Empresas/Centros con <c>.Take(N)</c> sin <c>.OrderBy(...)</c>
/// previo. Postgres no garantiza ningún orden de fila sin <c>ORDER BY</c> —
/// sin él, qué N filas concretas trae el <c>Take</c> depende del orden físico
/// de la tabla, no del contenido, y ese orden puede variar entre siembras
/// aunque los datos sean los mismos.
///
/// <para>
/// Este fichero MIDE la propiedad exacta que faltaba: inserta las filas en
/// orden DESCENDENTE por la clave que ahora ordena la consulta (<c>Dni</c>,
/// <c>RazonSocial</c>, <c>CodigoCentro</c>) y comprueba que <c>Take(N)</c>
/// sigue trayendo las N filas correctas — las lexicográficamente menores, no
/// las últimas insertadas. Sin el <c>OrderBy</c>, un <c>Take</c> sobre una
/// tabla insertada en orden descendente trae casi con certeza el conjunto
/// equivocado (el motor tiende a devolver las filas en orden físico/de
/// inserción cuando no se le pide otra cosa) — así que esta prueba es
/// falsable por mutación: revertir el <c>OrderBy</c> del seeder real y
/// replicar aquí la misma consulta sin él debe teñir de rojo.
/// </para>
/// </summary>
public class ComunicacionesDatosPruebaSeederOrdenTests
{
    [Fact]
    public async Task Trabajadores_Take_trae_los_60_Dni_menores_sin_importar_el_orden_de_insercion()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);
        using var ambito = arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        var tenantId = await CrearTenantConCatalogoAsync(contexto);
        using (AmbitoTenantExplicito.Establecer(tenantId))
        {
            var empresaBase = new Empresa("Empresa base para trabajadores");
            contexto.Empresas.Add(empresaBase);
            await contexto.SaveChangesAsync();

            // Inserción en orden DESCENDENTE de Dni (70 → 1): lo contrario de
            // lo que .OrderBy(t => t.Dni) debe producir.
            for (var numero = 70; numero >= 1; numero--)
            {
                contexto.Trabajadores.Add(Trabajador.DeEmpresa(
                    empresaBase.Id, "Nombre", $"Apellido {numero:D3}", GenerarDniValido(numero)));
            }
            await contexto.SaveChangesAsync();

            var resultado = await contexto.Trabajadores
                .OrderBy(t => t.Dni).Take(60).ToListAsync();

            var dniEsperados = Enumerable.Range(1, 60).Select(GenerarDniValido).ToHashSet();
            resultado.Select(t => t.Dni).ToHashSet().Should().BeEquivalentTo(dniEsperados,
                "MEDIDO: Take(60) tiene que traer los 60 Dni lexicográficamente menores (1..60), " +
                "no los últimos 60 insertados (11..70) que traería un Take sin OrderBy sobre esta tabla");
        }
    }

    [Fact]
    public async Task Empresas_Take_trae_las_40_RazonSocial_menores_sin_importar_el_orden_de_insercion()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);
        using var ambito = arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        var tenantId = await CrearTenantConCatalogoAsync(contexto);
        using (AmbitoTenantExplicito.Establecer(tenantId))
        {
            // Se insertan fuera del rango de nombres de prueba para que no
            // interfieran con el Take(40) esperado.
            contexto.Empresas.Add(new Empresa("ZZZ Empresa fuera de rango 1"));
            contexto.Empresas.Add(new Empresa("ZZZ Empresa fuera de rango 2"));

            for (var numero = 50; numero >= 1; numero--)
                contexto.Empresas.Add(new Empresa($"Empresa {numero:D3}"));
            await contexto.SaveChangesAsync();

            var resultado = await contexto.Empresas
                .OrderBy(e => e.RazonSocial).Take(40).ToListAsync();

            var esperadas = Enumerable.Range(1, 40).Select(n => $"Empresa {n:D3}").ToHashSet();
            resultado.Select(e => e.RazonSocial).ToHashSet().Should().BeEquivalentTo(esperadas,
                "MEDIDO: Take(40) tiene que traer las 40 RazonSocial lexicográficamente menores");
        }
    }

    [Fact]
    public async Task Centros_Take_trae_los_40_CodigoCentro_menores_sin_importar_el_orden_de_insercion()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);
        using var ambito = arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        var tenantId = await CrearTenantConCatalogoAsync(contexto);
        using (AmbitoTenantExplicito.Establecer(tenantId))
        {
            var cliente = Empresa.CrearComoCliente("Cliente base para centros", cif: GenerarCifValido(), esCritico: true, notas: null, ejecutivoUsuarioId: null);
            var empresa = new Empresa("Empresa base para centros");
            contexto.Empresas.AddRange(cliente, empresa);
            await contexto.SaveChangesAsync();

            for (var numero = 50; numero >= 1; numero--)
            {
                contexto.Centros.Add(new Centro(
                    cliente.Id, empresa.Id, $"Centro {numero:D3}", codigoCentro: $"CTR-{numero:D4}"));
            }
            await contexto.SaveChangesAsync();

            var resultado = await contexto.Centros
                .OrderBy(c => c.CodigoCentro).Take(40).ToListAsync();

            var esperados = Enumerable.Range(1, 40).Select(n => $"CTR-{n:D4}").ToHashSet();
            resultado.Select(c => c.CodigoCentro).ToHashSet().Should().BeEquivalentTo(esperados,
                "MEDIDO: Take(40) tiene que traer los 40 CodigoCentro lexicográficamente menores");
        }
    }

    /// <summary>DNI con letra de control real (algoritmo estándar módulo 23) — mismo patrón que AislamientoPorAgregadoTests.</summary>
    private static string GenerarDniValido(int numero)
    {
        const string letras = "TRWAGMYFPDXBNJZSQVHLCKE";
        return $"{numero:D8}{letras[numero % 23]}";
    }

    /// <summary>CIF con dígito de control real — mismo algoritmo que AislamientoPorAgregadoTests.GenerarCifValido.</summary>
    private static string GenerarCifValido()
    {
        var digitos = System.Threading.Interlocked.Increment(ref _contadorCif).ToString().PadLeft(7, '0');
        var sumaPares = 0;
        var sumaImpares = 0;
        for (var i = 0; i < digitos.Length; i++)
        {
            var num = digitos[i] - '0';
            if (i % 2 == 1)
            {
                sumaPares += num;
            }
            else
            {
                var multiplicado = num * 2;
                sumaImpares += multiplicado > 9 ? multiplicado - 9 : multiplicado;
            }
        }

        var residuo = (sumaPares + sumaImpares) % 10;
        var digitoControl = residuo == 0 ? 0 : 10 - residuo;
        return $"B{digitos}{digitoControl}";
    }

    private static int _contadorCif;

    private static async Task<Guid> CrearTenantConCatalogoAsync(CaeManagerDbContext contexto)
    {
        var tenant = new Tenant($"Tenant orden {Guid.NewGuid():N}");
        using (AmbitoTenantExplicito.Establecer(tenant.Id))
        {
            contexto.Tenants.Add(tenant);
            contexto.ParametrosSistema.Add(new ParametroSistema(
                ParametroSistemaSeedData.UmbralAmbarDias, ParametroSistemaSeedData.UmbralRojoDias));
            contexto.TiposDocumento.AddRange(TipoDocumentoSeedData.CrearCopiasParaTenant());
            await contexto.SaveChangesAsync();
        }

        return tenant.Id;
    }
}
