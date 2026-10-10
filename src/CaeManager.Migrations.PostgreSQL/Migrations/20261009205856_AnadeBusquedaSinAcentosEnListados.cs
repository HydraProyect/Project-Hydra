using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// El buscador de los listados ignora acentos además de mayúsculas: «garcia» encuentra
    /// «García». Hasta aquí comparaba <c>upper(columna) LIKE upper(término)</c>.
    ///
    /// <para>
    /// <b>Dos funciones</b>, las dos en <c>public</c> y con el cuerpo resuelto al crearlas
    /// (<c>RETURN</c>, no texto): <c>texto_de_busqueda</c> normaliza una columna
    /// (<c>unaccent</c> y después <c>upper</c>; en ese orden, porque «ß» pasa a «ss») y
    /// <c>patron_de_busqueda</c> normaliza el término, escapa los comodines y lo envuelve en
    /// <c>%…%</c>. Se declaran <c>IMMUTABLE</c> aunque <c>unaccent</c> es <c>STABLE</c>: es lo
    /// que permite indexar la primera, y se sostiene mientras no cambie el fichero de reglas
    /// del diccionario. Si una versión mayor de PostgreSQL lo cambia, los nueve índices de
    /// abajo se reconstruyen con <c>REINDEX</c>. Se llama a <c>unaccent</c> con el diccionario
    /// calificado por esquema para no depender del <c>search_path</c> de quien consulta.
    /// </para>
    ///
    /// <para>
    /// <b>Permisos.</b> Las funciones conservan el <c>EXECUTE</c> de <c>PUBLIC</c> que
    /// PostgreSQL da por defecto, igual que las de <c>pg_trgm</c>: no leen ninguna tabla y
    /// se ejecutan con los permisos de quien las llama.
    /// </para>
    ///
    /// <para>
    /// <b>Índices.</b> Se crea el trigram sobre la expresión nueva en las nueve columnas que
    /// ya lo tenían sobre <c>upper(columna)</c>. De los antiguos se retiran cinco, los que
    /// solo servían a los listados de Trabajadores y de Vehículos, y se conservan cuatro
    /// (<c>Centros.Nombre</c>, <c>Empresas.RazonSocial</c>, <c>Trabajadores.Nombre</c> y
    /// <c>Trabajadores.Apellidos</c>) porque el buscador global, los listados de
    /// Asignaciones e Incidencias y la comprobación de duplicados de Centro y de Empresa
    /// siguen comparando con <c>upper(columna)</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Lo que estos índices no hacen.</b> Medido en PostgreSQL 17.10 con una tabla bajo RLS
    /// leída como <c>cae_app_runtime</c>: el planificador no usa un índice trigram para un
    /// <c>LIKE</c>, ni los antiguos ni estos, porque <c>LIKE</c> no es <c>LEAKPROOF</c> y no
    /// puede evaluarse antes que la política de la tabla. Sirven a quien consulta sin RLS; en
    /// el tráfico de la aplicación la búsqueda filtra las filas que la política ya dejó pasar.
    /// Marcar algo <c>LEAKPROOF</c> para cambiarlo es una decisión de seguridad, no un ajuste
    /// de rendimiento. <b>No está medido en PostgreSQL 18.6</b>, que es la imagen de CI, de
    /// staging y de producción: la medición es del clúster local, y lo que haga el planificador
    /// de la 18 con estos índices bajo RLS está por comprobar.
    /// </para>
    /// </summary>
    public partial class AnadeBusquedaSinAcentosEnListados : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE EXTENSION IF NOT EXISTS unaccent WITH SCHEMA public;

                CREATE FUNCTION public.texto_de_busqueda(texto text) RETURNS text
                    LANGUAGE sql IMMUTABLE PARALLEL SAFE
                    RETURN pg_catalog.upper(public.unaccent('public.unaccent'::regdictionary, texto));

                CREATE FUNCTION public.patron_de_busqueda(termino text) RETURNS text
                    LANGUAGE sql IMMUTABLE PARALLEL SAFE
                    RETURN '%' || pg_catalog.replace(pg_catalog.replace(pg_catalog.replace(public.texto_de_busqueda(termino), '\', '\\'), '%', '\%'), '_', '\_') || '%';

                CREATE INDEX "IX_Centros_Nombre_Busqueda" ON public."Centros" USING gin (public.texto_de_busqueda(("Nombre")::text) public.gin_trgm_ops);
                CREATE INDEX "IX_Empresas_RazonSocial_Busqueda" ON public."Empresas" USING gin (public.texto_de_busqueda(("RazonSocial")::text) public.gin_trgm_ops);
                CREATE INDEX "IX_Trabajadores_Alias_Busqueda" ON public."Trabajadores" USING gin (public.texto_de_busqueda(("Alias")::text) public.gin_trgm_ops);
                CREATE INDEX "IX_Trabajadores_Apellidos_Busqueda" ON public."Trabajadores" USING gin (public.texto_de_busqueda(("Apellidos")::text) public.gin_trgm_ops);
                CREATE INDEX "IX_Trabajadores_Dni_Busqueda" ON public."Trabajadores" USING gin (public.texto_de_busqueda(("Dni")::text) public.gin_trgm_ops);
                CREATE INDEX "IX_Trabajadores_Nombre_Busqueda" ON public."Trabajadores" USING gin (public.texto_de_busqueda(("Nombre")::text) public.gin_trgm_ops);
                CREATE INDEX "IX_Vehiculos_Modelo_Busqueda" ON public."Vehiculos" USING gin (public.texto_de_busqueda(("Modelo")::text) public.gin_trgm_ops);
                CREATE INDEX "IX_Vehiculos_Nombre_Busqueda" ON public."Vehiculos" USING gin (public.texto_de_busqueda(("Nombre")::text) public.gin_trgm_ops);
                CREATE INDEX "IX_Vehiculos_NumeroPlaca_Busqueda" ON public."Vehiculos" USING gin (public.texto_de_busqueda(("NumeroPlaca")::text) public.gin_trgm_ops);

                DROP INDEX public."IX_Trabajadores_Alias_Trgm";
                DROP INDEX public."IX_Trabajadores_Dni_Trgm";
                DROP INDEX public."IX_Vehiculos_Modelo_Trgm";
                DROP INDEX public."IX_Vehiculos_Nombre_Trgm";
                DROP INDEX public."IX_Vehiculos_NumeroPlaca_Trgm";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE INDEX "IX_Trabajadores_Alias_Trgm" ON public."Trabajadores" USING gin (upper(("Alias")::text) public.gin_trgm_ops);
                CREATE INDEX "IX_Trabajadores_Dni_Trgm" ON public."Trabajadores" USING gin (upper(("Dni")::text) public.gin_trgm_ops);
                CREATE INDEX "IX_Vehiculos_Modelo_Trgm" ON public."Vehiculos" USING gin (upper(("Modelo")::text) public.gin_trgm_ops);
                CREATE INDEX "IX_Vehiculos_Nombre_Trgm" ON public."Vehiculos" USING gin (upper(("Nombre")::text) public.gin_trgm_ops);
                CREATE INDEX "IX_Vehiculos_NumeroPlaca_Trgm" ON public."Vehiculos" USING gin (upper(("NumeroPlaca")::text) public.gin_trgm_ops);

                DROP INDEX public."IX_Centros_Nombre_Busqueda";
                DROP INDEX public."IX_Empresas_RazonSocial_Busqueda";
                DROP INDEX public."IX_Trabajadores_Alias_Busqueda";
                DROP INDEX public."IX_Trabajadores_Apellidos_Busqueda";
                DROP INDEX public."IX_Trabajadores_Dni_Busqueda";
                DROP INDEX public."IX_Trabajadores_Nombre_Busqueda";
                DROP INDEX public."IX_Vehiculos_Modelo_Busqueda";
                DROP INDEX public."IX_Vehiculos_Nombre_Busqueda";
                DROP INDEX public."IX_Vehiculos_NumeroPlaca_Busqueda";

                DROP FUNCTION public.patron_de_busqueda(text);
                DROP FUNCTION public.texto_de_busqueda(text);

                DROP EXTENSION unaccent;
                """);
        }
    }
}
