using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Privilegios del rol de base de datos <c>cae_app_aprovisionamiento</c>
    /// (PD-A3): escritura ACOTADA para la capacidad <c>Aprovisionamiento</c>,
    /// nunca <c>ALL TABLES</c>. El rol en sí lo crea el bootstrap de clúster
    /// (<c>deploy/bootstrap/roles-de-cluster.sql</c>), igual que
    /// <c>cae_app_soporte</c> — aquí solo van los privilegios de ESTA base.
    ///
    /// <b>Lista literal, no "todas las tablas que lleven TenantId".</b> El
    /// invariante que sostiene esta migración es: ningún GRANT de escritura
    /// sobre una tabla sin política RLS. Verificado tabla por tabla contra el
    /// código real: <c>Empresas</c>/<c>Centros</c>/<c>Trabajadores</c> (núcleo
    /// del alta), <c>RelacionesEmpresariales</c> (la arista
    /// proveedora×cliente — el UPDATE lo exige
    /// <c>EjecutarImportacionCombinadaCommand.CerrarVigenteAsync</c>),
    /// <c>TiposDocumento</c>/<c>TiposDocumentoCentros</c> (catálogo documental
    /// del alta), <c>Asignaciones</c> (vínculo trabajador-centro),
    /// <c>OperacionesImportacion</c> (idempotencia de la importación),
    /// <c>Documentos</c> (SELECT/INSERT, nunca UPDATE:
    /// <c>EjecutarImportacionCommand</c> lee para deduplicar por
    /// (TrabajadorId, TipoDocumentoId) e inserta los nuevos; el único
    /// encuentro con un documento preexistente hace <c>continue</c>, nunca lo
    /// muta), <c>HistorialImportaciones</c> (SELECT/INSERT — lo escribe
    /// <c>RegistrarHistorialImportacionCommand</c>, que el flujo de
    /// aprovisionamiento dispara siempre tras importar) y
    /// <c>RegistrosAuditoria</c> (SELECT + INSERT solo, nunca UPDATE — es
    /// inmutable).
    ///
    /// Sin <c>DELETE</c> en ninguna línea: aprovisionar es dar de alta, la
    /// baja lógica del dominio es un UPDATE de <c>EstaEliminado</c>.
    ///
    /// Fuera de la lista, con motivo, y NUNCA concedidas (no
    /// concedidas-y-revocadas): Identity completo (el aprovisionamiento no
    /// crea usuarios), <c>Tenants</c>/<c>DelegacionesTenant</c>/asignaciones
    /// operativas (plano 3 de administración, no contenido CAE), las cuatro
    /// tablas de concesión/sesión privilegiada (<c>ConcesionesPrivilegio</c>,
    /// <c>SesionesPrivilegiadas</c>, <c>TenantsAlcanzadosPorConcesion</c>,
    /// <c>EstadoBootstrapPlataforma</c>) y las tablas de secretos del tenant
    /// (<c>CredencialesAccesoEmpresa</c>, <c>SellosEmpresa</c> — coherente con
    /// que <c>AutorizacionSecretosDeTenantBehavior</c> ya niega su lectura a
    /// todo el plano 3).
    ///
    /// Sin <c>ALTER DEFAULT PRIVILEGES</c> a propósito, y asimétrico respecto
    /// a <c>RolSoporteSoloLectura</c>: allí una tabla futura legible por
    /// soporte es la postura correcta; aquí una tabla futura escribible por
    /// defecto sería exactamente el defecto que este diseño evita.
    ///
    /// Sin RLS que habilitar aparte: <c>RelacionesEmpresariales</c>
    /// (20260826153714_AgregarRelacionEmpresarial) y
    /// <c>OperacionesImportacion</c> (20260903040338_AgregarOperacionImportacion)
    /// ya traen RLS + FORCE + política <c>aislamiento_tenant</c> desde su
    /// propia migración de creación; el resto está en la lista literal de
    /// <c>HabilitarRlsPostgres</c> o su propia migración de alta.
    /// </summary>
    public partial class RolAprovisionamientoEscrituraAcotada : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $$
BEGIN
    -- El rol lo provee el BOOTSTRAP DE CLÚSTER
    -- (deploy/bootstrap/roles-de-cluster.sql), no esta migración — mismo
    -- motivo que RolSoporteSoloLectura: pg_authid es un catálogo compartido.
    -- Si el bootstrap no se ejecutó, esto falla con 42704 inmediato.

    GRANT USAGE ON SCHEMA public TO cae_app_aprovisionamiento;

    GRANT SELECT, INSERT, UPDATE ON ""Empresas"" TO cae_app_aprovisionamiento;
    GRANT SELECT, INSERT, UPDATE ON ""Centros"" TO cae_app_aprovisionamiento;
    GRANT SELECT, INSERT, UPDATE ON ""Trabajadores"" TO cae_app_aprovisionamiento;
    GRANT SELECT, INSERT, UPDATE ON ""RelacionesEmpresariales"" TO cae_app_aprovisionamiento;
    GRANT SELECT, INSERT ON ""TiposDocumento"" TO cae_app_aprovisionamiento;
    GRANT SELECT, INSERT ON ""TiposDocumentoCentros"" TO cae_app_aprovisionamiento;
    GRANT SELECT, INSERT ON ""Asignaciones"" TO cae_app_aprovisionamiento;
    GRANT SELECT, INSERT ON ""OperacionesImportacion"" TO cae_app_aprovisionamiento;
    GRANT SELECT, INSERT ON ""Documentos"" TO cae_app_aprovisionamiento;
    GRANT SELECT, INSERT ON ""HistorialImportaciones"" TO cae_app_aprovisionamiento;
    GRANT SELECT, INSERT ON ""RegistrosAuditoria"" TO cae_app_aprovisionamiento;
END $$;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'cae_app_aprovisionamiento') THEN
        REVOKE ALL PRIVILEGES ON ""Empresas"" FROM cae_app_aprovisionamiento;
        REVOKE ALL PRIVILEGES ON ""Centros"" FROM cae_app_aprovisionamiento;
        REVOKE ALL PRIVILEGES ON ""Trabajadores"" FROM cae_app_aprovisionamiento;
        REVOKE ALL PRIVILEGES ON ""RelacionesEmpresariales"" FROM cae_app_aprovisionamiento;
        REVOKE ALL PRIVILEGES ON ""TiposDocumento"" FROM cae_app_aprovisionamiento;
        REVOKE ALL PRIVILEGES ON ""TiposDocumentoCentros"" FROM cae_app_aprovisionamiento;
        REVOKE ALL PRIVILEGES ON ""Asignaciones"" FROM cae_app_aprovisionamiento;
        REVOKE ALL PRIVILEGES ON ""OperacionesImportacion"" FROM cae_app_aprovisionamiento;
        REVOKE ALL PRIVILEGES ON ""Documentos"" FROM cae_app_aprovisionamiento;
        REVOKE ALL PRIVILEGES ON ""HistorialImportaciones"" FROM cae_app_aprovisionamiento;
        REVOKE ALL PRIVILEGES ON ""RegistrosAuditoria"" FROM cae_app_aprovisionamiento;
        REVOKE USAGE ON SCHEMA public FROM cae_app_aprovisionamiento;
        -- El rol NO se borra aquí: es objeto de clúster (ver el Up).
    END IF;
END $$;
");
        }
    }
}
