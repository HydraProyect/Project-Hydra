using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class LineaBase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Alertas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    Nivel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FechaGeneracionUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Alertas", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AprobacionesDocumento",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    Tipo = table.Column<int>(type: "integer", nullable: false),
                    ConfianzaGeneral = table.Column<int>(type: "integer", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AprobacionesDocumento", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Asignaciones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TrabajadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    CentroId = table.Column<Guid>(type: "uuid", nullable: false),
                    FechaAlta = table.Column<DateOnly>(type: "date", nullable: false),
                    FechaBaja = table.Column<DateOnly>(type: "date", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Asignaciones", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AsignacionesOperadorDelegado",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DelegacionTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    Rol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AsignacionesOperadorDelegado", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NombreCompleto = table.Column<string>(type: "text", nullable: false),
                    Tema = table.Column<int>(type: "integer", nullable: false),
                    CoordinadorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClienteId = table.Column<Guid>(type: "uuid", nullable: true),
                    DebeCambiarContrasena = table.Column<bool>(type: "boolean", nullable: false),
                    FechaCreacion = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: true),
                    SecurityStamp = table.Column<string>(type: "text", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true),
                    PhoneNumber = table.Column<string>(type: "text", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditoriasExtraccionIa",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HashSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TipoEsperado = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    ProveedorCodigo = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TiempoProcesamientoMs = table.Column<long>(type: "bigint", nullable: false),
                    CosteEstimadoOcr = table.Column<decimal>(type: "numeric(10,6)", precision: 10, scale: 6, nullable: true),
                    CosteEstimado = table.Column<decimal>(type: "numeric(10,6)", precision: 10, scale: 6, nullable: true),
                    NumeroPaginas = table.Column<int>(type: "integer", nullable: false),
                    ConfianzaGeneral = table.Column<int>(type: "integer", nullable: false),
                    Incidencias = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditoriasExtraccionIa", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CanalesGestionDocumental",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CentroId = table.Column<Guid>(type: "uuid", nullable: false),
                    Tipo = table.Column<int>(type: "integer", nullable: false),
                    NombrePlataforma = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    UrlAcceso = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Usuario = table.Column<string>(type: "text", nullable: true),
                    Contrasena = table.Column<string>(type: "text", nullable: true),
                    EmailsDestinatarios = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    NombreContacto = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    Notas = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CanalesGestionDocumental", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Centros",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClienteId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmpresaId = table.Column<Guid>(type: "uuid", nullable: false),
                    Nombre = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CodigoCentro = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Direccion = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Contacto = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ContratoVigenteHasta = table.Column<DateOnly>(type: "date", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Centros", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Clientes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RazonSocial = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Cif = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    EsCritico = table.Column<bool>(type: "boolean", nullable: false),
                    Notas = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    EjecutivoUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clientes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConfiguracionesIaDocumentoCliente",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClienteId = table.Column<Guid>(type: "uuid", nullable: false),
                    TipoDocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    Activa = table.Column<bool>(type: "boolean", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfiguracionesIaDocumentoCliente", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConversacionesCorreo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClienteId = table.Column<Guid>(type: "uuid", nullable: true),
                    Asunto = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Estado = table.Column<int>(type: "integer", nullable: false),
                    EjecutivoAsignadoId = table.Column<Guid>(type: "uuid", nullable: true),
                    Etiquetas = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FechaUltimoMensajeUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversacionesCorreo", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CredencialesAccesoEmpresa",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmpresaId = table.Column<Guid>(type: "uuid", nullable: false),
                    UrlAcceso = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CampoEmpresa = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Usuario = table.Column<string>(type: "text", nullable: true),
                    Contrasena = table.Column<string>(type: "text", nullable: true),
                    Notas = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CredencialesAccesoEmpresa", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CredencialesAccesoSubcontrata",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubcontrataId = table.Column<Guid>(type: "uuid", nullable: false),
                    UrlAcceso = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CampoEmpresa = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Usuario = table.Column<string>(type: "text", nullable: true),
                    Contrasena = table.Column<string>(type: "text", nullable: true),
                    Notas = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CredencialesAccesoSubcontrata", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DelegacionesTenant",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantConsultoraId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantClienteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Activa = table.Column<bool>(type: "boolean", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Proposito = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    MotivoActivacion = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ExpiraEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ActivadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DelegacionesTenant", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeteccionesTrabajador",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmpresaId = table.Column<Guid>(type: "uuid", nullable: false),
                    Tipo = table.Column<int>(type: "integer", nullable: false),
                    Nombre = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Apellidos = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Dni = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TrabajadorExistenteId = table.Column<Guid>(type: "uuid", nullable: true),
                    Resuelta = table.Column<bool>(type: "boolean", nullable: false),
                    AccionTomada = table.Column<string>(type: "text", nullable: true),
                    CreadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeteccionesTrabajador", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Documentos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TrabajadorId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClienteId = table.Column<Guid>(type: "uuid", nullable: true),
                    EmpresaId = table.Column<Guid>(type: "uuid", nullable: true),
                    VehiculoId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProyectoId = table.Column<Guid>(type: "uuid", nullable: true),
                    TipoDocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    FechaEmision = table.Column<DateOnly>(type: "date", nullable: false),
                    FechaVencimiento = table.Column<DateOnly>(type: "date", nullable: true),
                    ArchivoUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Comentarios = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    AnonimizadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Documentos", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Empresas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RazonSocial = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Cif = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Empresas", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EmpresasClientes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmpresaId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClienteId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmpresasClientes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Evaluaciones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CentroId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrabajadorId = table.Column<Guid>(type: "uuid", nullable: true),
                    Fecha = table.Column<DateOnly>(type: "date", nullable: false),
                    Puntuacion = table.Column<int>(type: "integer", nullable: false),
                    Observaciones = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Evaluaciones", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExtraccionesIaCache",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HashSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExtraccionJson = table.Column<string>(type: "text", nullable: false),
                    CreadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExtraccionesIaCache", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Incidencias",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CentroId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrabajadorId = table.Column<Guid>(type: "uuid", nullable: true),
                    Tipo = table.Column<string>(type: "text", nullable: false),
                    Gravedad = table.Column<string>(type: "text", nullable: false),
                    FechaOcurrencia = table.Column<DateOnly>(type: "date", nullable: false),
                    Descripcion = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Resuelta = table.Column<bool>(type: "boolean", nullable: false),
                    ResueltaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Incidencias", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MacrosRespuesta",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClienteId = table.Column<Guid>(type: "uuid", nullable: true),
                    Titulo = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    CuerpoHtml = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MacrosRespuesta", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificacionesUsuario",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioDestinatarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    Titulo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Mensaje = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    CreadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Leida = table.Column<bool>(type: "boolean", nullable: false),
                    UrlAccion = table.Column<string>(type: "text", nullable: true),
                    TextoAccion = table.Column<string>(type: "text", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificacionesUsuario", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ParametrosSistema",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UmbralAmbarDias = table.Column<int>(type: "integer", nullable: false),
                    UmbralRojoDias = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParametrosSistema", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Proyectos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClienteId = table.Column<Guid>(type: "uuid", nullable: false),
                    CentroId = table.Column<Guid>(type: "uuid", nullable: false),
                    Nombre = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FechaInicio = table.Column<DateOnly>(type: "date", nullable: false),
                    FechaFinPrevista = table.Column<DateOnly>(type: "date", nullable: true),
                    FechaCierreReal = table.Column<DateOnly>(type: "date", nullable: true),
                    Notas = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Proyectos", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProyectosTecnicos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProyectoId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrabajadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    FechaAlta = table.Column<DateOnly>(type: "date", nullable: false),
                    FechaBaja = table.Column<DateOnly>(type: "date", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProyectosTecnicos", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RegistrosActividadSoporte",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioSoporteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Tipo = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    DelegacionTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Detalle = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    OcurridaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistrosActividadSoporte", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RegistrosAuditoria",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EntidadTipo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EntidadId = table.Column<Guid>(type: "uuid", nullable: false),
                    Accion = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DatosAntes = table.Column<string>(type: "TEXT", nullable: true),
                    DatosDespues = table.Column<string>(type: "TEXT", nullable: true),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    FechaUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistrosAuditoria", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RequisitosDocumentales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CentroId = table.Column<Guid>(type: "uuid", nullable: false),
                    Descripcion = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    PeriodicidadEspecial = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    BloqueaAcceso = table.Column<bool>(type: "boolean", nullable: false),
                    Notas = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequisitosDocumentales", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RevisionesIaDocumento",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConfianzaGeneral = table.Column<int>(type: "integer", nullable: false),
                    TipoDetectado = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    FechaEmisionDetectada = table.Column<DateOnly>(type: "date", nullable: true),
                    FechaVencimientoDetectada = table.Column<DateOnly>(type: "date", nullable: true),
                    TieneFirmaDetectada = table.Column<bool>(type: "boolean", nullable: true),
                    Motivo = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Resuelta = table.Column<bool>(type: "boolean", nullable: false),
                    CreadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RevisionesIaDocumento", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SolicitudesPurga",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TipoDato = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    RegistrosAfectados = table.Column<int>(type: "integer", nullable: false),
                    FechaCorte = table.Column<DateOnly>(type: "date", nullable: false),
                    Estado = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    DetectadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AvisadaAlTenantEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FechaEjecucionProgramada = table.Column<DateOnly>(type: "date", nullable: true),
                    AutorizadaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    EjecutadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Motivo = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SolicitudesPurga", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Subcontratas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RazonSocial = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subcontratas", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SubcontratasClientes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubcontrataId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClienteId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubcontratasClientes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SubcontratasEmpresas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubcontrataId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmpresaId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubcontratasEmpresas", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TarifasCliente",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClienteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Concepto = table.Column<int>(type: "integer", nullable: false),
                    PrecioUnitario = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    MonedaIso = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TarifasCliente", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Nombre = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Estado = table.Column<string>(type: "text", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EsPlataforma = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TiposDocumento",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Nombre = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    VigenciaMeses = table.Column<int>(type: "integer", nullable: true),
                    AplicaVencimientoAutomatico = table.Column<bool>(type: "boolean", nullable: false),
                    Notas = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Orden = table.Column<int>(type: "integer", nullable: false),
                    Descripcion = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CriteriosValidacion = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SeSolicitaA = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Observaciones = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    EsObligatorio = table.Column<bool>(type: "boolean", nullable: false),
                    AmbitoAplicacion = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LecturaIaActiva = table.Column<bool>(type: "boolean", nullable: false),
                    DeteccionTrabajadoresActiva = table.Column<bool>(type: "boolean", nullable: false),
                    VerificacionIaActiva = table.Column<bool>(type: "boolean", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TiposDocumento", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TiposDocumentoCentros",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TipoDocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    CentroId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TiposDocumentoCentros", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Trabajadores",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmpresaId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubcontrataId = table.Column<Guid>(type: "uuid", nullable: true),
                    Nombre = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Apellidos = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Alias = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    Dni = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FechaNacimiento = table.Column<DateOnly>(type: "date", nullable: true),
                    Email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Observaciones = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    AnonimizadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Trabajadores", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Vehiculos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmpresaId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubcontrataId = table.Column<Guid>(type: "uuid", nullable: true),
                    Nombre = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Modelo = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NumeroPlaca = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vehiculos", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Visitas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CentroId = table.Column<Guid>(type: "uuid", nullable: false),
                    FechaInicio = table.Column<DateOnly>(type: "date", nullable: false),
                    FechaFin = table.Column<DateOnly>(type: "date", nullable: false),
                    NotificadoCliente = table.Column<bool>(type: "boolean", nullable: false),
                    Notas = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Visitas", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VisitasTrabajadores",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VisitaId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrabajadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VisitasTrabajadores", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    ProviderKey = table.Column<string>(type: "text", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "text", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MensajesCorreo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversacionCorreoId = table.Column<Guid>(type: "uuid", nullable: false),
                    Direccion = table.Column<int>(type: "integer", nullable: false),
                    RemitenteEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    CuerpoHtml = table.Column<string>(type: "text", nullable: false),
                    FechaUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MensajesCorreo", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MensajesCorreo_ConversacionesCorreo_ConversacionCorreoId",
                        column: x => x.ConversacionCorreoId,
                        principalTable: "ConversacionesCorreo",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ParticipantesConversacion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversacionCorreoId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Rol = table.Column<int>(type: "integer", nullable: false),
                    TipoOrigen = table.Column<int>(type: "integer", nullable: false),
                    EntidadRelacionadaId = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParticipantesConversacion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ParticipantesConversacion_ConversacionesCorreo_Conversacion~",
                        column: x => x.ConversacionCorreoId,
                        principalTable: "ConversacionesCorreo",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "AspNetRoles",
                columns: new[] { "Id", "ConcurrencyStamp", "Name", "NormalizedName" },
                values: new object[,]
                {
                    { new Guid("30000000-0000-0000-0000-000000000001"), "30000000-0000-0000-0000-000000000001", "Administrador", "ADMINISTRADOR" },
                    { new Guid("30000000-0000-0000-0000-000000000002"), "30000000-0000-0000-0000-000000000002", "CoordinadorCae", "COORDINADORCAE" },
                    { new Guid("30000000-0000-0000-0000-000000000003"), "30000000-0000-0000-0000-000000000003", "GestorCae", "GESTORCAE" },
                    { new Guid("30000000-0000-0000-0000-000000000004"), "30000000-0000-0000-0000-000000000004", "Consulta", "CONSULTA" },
                    { new Guid("30000000-0000-0000-0000-000000000005"), "30000000-0000-0000-0000-000000000005", "DireccionCae", "DIRECCIONCAE" },
                    { new Guid("30000000-0000-0000-0000-000000000006"), "30000000-0000-0000-0000-000000000006", "Cliente", "CLIENTE" }
                });

            migrationBuilder.InsertData(
                table: "ParametrosSistema",
                columns: new[] { "Id", "TenantId", "UmbralAmbarDias", "UmbralRojoDias" },
                values: new object[] { new Guid("20000000-0000-0000-0000-000000000001"), new Guid("00000000-0000-0000-0000-000000000001"), 30, 15 });

            migrationBuilder.InsertData(
                table: "Tenants",
                columns: new[] { "Id", "CreadoEnUtc", "EsPlataforma", "Estado", "Nombre" },
                values: new object[] { new Guid("00000000-0000-0000-0000-000000000001"), new DateTime(2026, 7, 23, 0, 0, 0, 0, DateTimeKind.Utc), true, "Activo", "Organización principal" });

            migrationBuilder.InsertData(
                table: "TiposDocumento",
                columns: new[] { "Id", "AmbitoAplicacion", "AplicaVencimientoAutomatico", "CriteriosValidacion", "Descripcion", "DeteccionTrabajadoresActiva", "EsObligatorio", "LecturaIaActiva", "Nombre", "Notas", "Observaciones", "Orden", "SeSolicitaA", "TenantId", "VerificacionIaActiva", "VigenciaMeses" },
                values: new object[,]
                {
                    { new Guid("10000000-0000-0000-0000-000000000001"), "Trabajador", true, null, null, false, false, true, "Apto médico laboral", "Renovación anual estándar.", null, 1, null, new Guid("00000000-0000-0000-0000-000000000001"), false, 12 },
                    { new Guid("10000000-0000-0000-0000-000000000002"), "Trabajador", true, null, null, false, false, true, "EPIS (firma)", "Se firman cada año según nota de origen.", null, 2, null, new Guid("00000000-0000-0000-0000-000000000001"), false, 12 },
                    { new Guid("10000000-0000-0000-0000-000000000003"), "Trabajador", true, null, null, false, false, true, "Reciclaje 4h", "Cada 4 años, según Dpto. Formación.", null, 3, null, new Guid("00000000-0000-0000-0000-000000000001"), false, 48 },
                    { new Guid("10000000-0000-0000-0000-000000000004"), "Trabajador", true, null, null, false, false, true, "Formación Art. 19", "Recordatorio cada 3 años.", null, 4, null, new Guid("00000000-0000-0000-0000-000000000001"), false, 36 },
                    { new Guid("10000000-0000-0000-0000-000000000005"), "Trabajador", false, null, null, false, false, true, "Formación 60h (base convenio)", "Formación base, no consta caducidad.", null, 5, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("10000000-0000-0000-0000-000000000006"), "Trabajador", false, null, null, false, false, true, "Formación 20h", "Mismo curso de convenio que 60h/6h, no consta caducidad.", null, 6, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("10000000-0000-0000-0000-000000000007"), "Trabajador", false, null, null, false, false, true, "Formación 6h", "Mismo curso de convenio que 60h/20h, no consta caducidad.", null, 7, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("10000000-0000-0000-0000-000000000008"), "Trabajador", false, null, null, false, false, true, "Información Art. 18", "No consta periodicidad de renovación.", null, 8, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("10000000-0000-0000-0000-000000000009"), "Trabajador", false, null, null, false, false, true, "Carretillas elevadoras", "Configurable si el convenio interno define vigencia.", null, 9, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("10000000-0000-0000-0000-00000000000a"), "Trabajador", false, null, null, false, false, true, "PEMP (plataformas elevadoras)", "Configurable si el convenio interno define vigencia.", null, 10, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("10000000-0000-0000-0000-00000000000b"), "Trabajador", false, null, null, false, false, true, "LOTO (4h)", "Configurable si el convenio interno define vigencia.", null, 11, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("10000000-0000-0000-0000-00000000000c"), "Trabajador", false, null, null, false, false, true, "Seguridad alimentaria", "Configurable si el convenio interno define vigencia.", null, 12, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("10000000-0000-0000-0000-00000000000d"), "Trabajador", false, null, null, false, false, true, "Primeros auxilios", "Se recomienda revisar cada 2 años; sin dato oficial de origen.", null, 13, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("10000000-0000-0000-0000-00000000000e"), "Trabajador", false, null, null, false, false, true, "Espacios confinados", "Configurable si el convenio interno define vigencia.", null, 14, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("10000000-0000-0000-0000-00000000000f"), "Trabajador", false, null, null, false, false, true, "Trabajos en altura (8h)", "Configurable si el convenio interno define vigencia.", null, 15, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("20000000-0000-0000-0000-000000000001"), "Empresa", true, null, null, false, true, true, "Certificado de estar al corriente con la Seguridad Social", "Mensual.", null, 16, null, new Guid("00000000-0000-0000-0000-000000000001"), false, 1 },
                    { new Guid("20000000-0000-0000-0000-000000000002"), "Empresa", false, null, null, false, true, true, "Certificado de estar al corriente con Hacienda", "Vigencia variable (1, 3, 6 o 12 meses según lo que exija el cliente) — la fecha de vencimiento se introduce a mano al subir el documento.", null, 17, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("20000000-0000-0000-0000-000000000003"), "Empresa", true, null, null, true, true, true, "ITA", "Mensual.", null, 18, null, new Guid("00000000-0000-0000-0000-000000000001"), false, 1 },
                    { new Guid("20000000-0000-0000-0000-000000000004"), "Empresa", true, null, null, false, true, true, "RLC/TC1", "Mensual — el documento de un periodo (p. ej. 01/05) vence 3 meses después (01/08), porque tarda en emitirse con la fecha del periodo ya pasada.", null, 19, null, new Guid("00000000-0000-0000-0000-000000000001"), false, 3 },
                    { new Guid("20000000-0000-0000-0000-000000000005"), "Empresa", true, null, null, false, true, true, "Recibo de pago RLC/TC1", "Mismo criterio de vigencia que el RLC/TC1.", null, 20, null, new Guid("00000000-0000-0000-0000-000000000001"), false, 3 },
                    { new Guid("20000000-0000-0000-0000-000000000006"), "Empresa", true, null, null, false, true, true, "RLC/TC1 + Recibo de pago", "Variante combinada — mismo criterio de vigencia que el RLC/TC1.", null, 21, null, new Guid("00000000-0000-0000-0000-000000000001"), false, 3 },
                    { new Guid("20000000-0000-0000-0000-000000000007"), "Empresa", true, null, null, true, true, true, "RNT/TC2", "Mismo criterio que el RLC/TC1.", null, 22, null, new Guid("00000000-0000-0000-0000-000000000001"), false, 3 },
                    { new Guid("20000000-0000-0000-0000-000000000008"), "Empresa", false, null, null, false, true, true, "Mutua", "Vigencia sin especificar — fecha de vencimiento manual.", null, 23, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("20000000-0000-0000-0000-000000000009"), "Empresa", false, null, null, false, true, true, "Seguro de Responsabilidad Civil + recibo de pago", "Vigencia sin especificar — fecha de vencimiento manual.", null, 24, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("2000000a-0000-0000-0000-00000000000a"), "Empresa", false, null, null, false, true, true, "SPA (Servicio de Prevención Ajeno)", "Debe venir acompañado de un certificado de pago que indica la fecha fin de validez — se introduce esa fecha manualmente.", null, 25, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("2000000b-0000-0000-0000-00000000000b"), "Empresa", false, null, null, false, true, true, "EVR (Evaluación de Riesgos Laborales)", "Vigencia sin especificar — fecha de vencimiento manual.", null, 26, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("2000000c-0000-0000-0000-00000000000c"), "Empresa", false, null, null, false, true, true, "PAP (Planificación de la Actividad Preventiva)", "Vigencia sin especificar — fecha de vencimiento manual.", null, 27, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("2000000d-0000-0000-0000-00000000000d"), "Empresa", false, null, null, false, false, true, "Tarjeta CIF", "Opcional — no obligatorio para todos los clientes.", null, 28, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("30000000-0000-0000-0000-000000000001"), "Vehiculo", false, null, null, false, true, true, "ITC", "Vigencia sin especificar — fecha de vencimiento manual.", null, 1, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("30000000-0000-0000-0000-000000000002"), "Vehiculo", false, null, null, false, true, true, "Ficha técnica", "No caduca por sí sola, pero se pide como documento adjunto del vehículo.", null, 2, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("30000000-0000-0000-0000-000000000003"), "Vehiculo", false, null, null, false, true, true, "Seguro", "Vigencia sin especificar — fecha de vencimiento manual.", null, 3, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("30000000-0000-0000-0000-000000000004"), "Vehiculo", false, null, null, false, true, true, "Autorización de circulación", "Vigencia sin especificar — fecha de vencimiento manual.", null, 4, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("40000000-0000-0000-0000-000000000001"), "Empresa", false, null, null, false, true, true, "Plan de Prevención", "Vigente con revisiones — vencimiento manual.", null, 29, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("40000000-0000-0000-0000-000000000002"), "Empresa", false, null, null, false, true, true, "Designación de Recursos Preventivos", "Vigente hasta modificación — vencimiento manual.", null, 30, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("40000000-0000-0000-0000-000000000003"), "Empresa", false, null, null, false, true, true, "Procedimiento de Coordinación de Actividades Empresariales", "Vigente hasta revisión — vencimiento manual.", null, 31, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("40000000-0000-0000-0000-000000000004"), "Empresa", false, null, null, false, false, true, "Política Preventiva", "Vigente hasta revisión — vencimiento manual.", null, 32, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("40000000-0000-0000-0000-000000000005"), "Empresa", false, null, null, false, false, true, "Organigrama Preventivo", "Vigente hasta cambios — vencimiento manual.", null, 33, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("40000000-0000-0000-0000-000000000006"), "Empresa", false, null, null, false, false, true, "Modalidad Preventiva", "Vigente hasta cambios — vencimiento manual.", null, 34, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("40000000-0000-0000-0000-000000000007"), "Empresa", false, null, null, false, false, true, "Escritura de Constitución", "Documento permanente — algunos clientes lo piden, no todos.", null, 35, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("40000000-0000-0000-0000-000000000008"), "Empresa", false, null, null, false, false, true, "Poder del Representante Legal", "Vigente hasta modificación — vencimiento manual.", null, 36, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("40000000-0000-0000-0000-000000000009"), "Empresa", false, null, null, false, false, true, "ISO 45001", "Certificación opcional — vigencia según auditoría del organismo certificador.", null, 37, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("4000000a-0000-0000-0000-00000000000a"), "Empresa", false, null, null, false, false, true, "ISO 9001", "Certificación opcional — vigencia según auditoría del organismo certificador.", null, 38, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("4000000b-0000-0000-0000-00000000000b"), "Empresa", false, null, null, false, false, true, "ISO 14001", "Certificación opcional — vigencia según auditoría del organismo certificador.", null, 39, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("4000000c-0000-0000-0000-00000000000c"), "Empresa", false, null, null, false, false, true, "Declaración Responsable CAE", "Vigencia según lo que exija cada cliente — vencimiento manual.", null, 40, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("4000000d-0000-0000-0000-00000000000d"), "Empresa", false, null, null, false, false, true, "Relación de Maquinaria", "Listado actualizable de la maquinaria de la empresa.", null, 41, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("4000000e-0000-0000-0000-00000000000e"), "Empresa", false, null, null, false, false, true, "VAT europeo", "Solo aplica a empresas extranjeras de la UE.", null, 42, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("4000000f-0000-0000-0000-00000000000f"), "Empresa", false, null, null, false, false, true, "Documento acreditativo de empresa extranjera", "Solo aplica a empresas extranjeras.", null, 43, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("40000010-0000-0000-0000-000000000010"), "Empresa", false, null, null, false, false, true, "Traducción jurada", "Solo si el cliente la solicita explícitamente para documentación de una empresa extranjera.", null, 44, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("40000011-0000-0000-0000-000000000011"), "Empresa", false, null, null, false, false, true, "Comunicación de desplazamiento", "Solo aplica cuando hay un desplazamiento temporal de trabajadores desde otro país de la UE.", null, 45, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("50000000-0000-0000-0000-000000000001"), "Trabajador", false, null, null, false, false, true, "Contrato de Trabajo", "Vigente mientras dure la relación laboral — sin fecha de caducidad propia.", null, 16, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("50000000-0000-0000-0000-000000000002"), "Trabajador", false, null, null, false, false, true, "Alta en Seguridad Social", "Vigente mientras continúe contratado — sin fecha de caducidad propia.", null, 17, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("50000000-0000-0000-0000-000000000003"), "Trabajador", false, null, null, false, false, true, "Formación Riesgos Específicos", "Vigente hasta cambio de puesto o de riesgos — vencimiento manual.", null, 18, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("50000000-0000-0000-0000-000000000004"), "Trabajador", false, null, null, false, false, true, "Formación EPIs", "Distinto de \"EPIS (firma)\" (la entrega/firma de recepción) — esta es la formación de uso.", null, 19, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("50000000-0000-0000-0000-000000000005"), "Trabajador", false, null, null, false, false, true, "Permiso de conducir", "Vigencia según DGT, muy variable — vencimiento manual.", null, 20, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("50000000-0000-0000-0000-000000000006"), "Trabajador", true, null, null, false, false, true, "Riesgo Eléctrico", "Renovación cada 3 años, criterio habitual del sector.", null, 21, null, new Guid("00000000-0000-0000-0000-000000000001"), false, 36 },
                    { new Guid("50000000-0000-0000-0000-000000000007"), "Trabajador", false, null, null, false, false, true, "Manipulación Manual de Cargas", "Vigencia según política de cada empresa — vencimiento manual.", null, 22, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("50000000-0000-0000-0000-000000000008"), "Trabajador", false, null, null, false, false, true, "Manipulación de Productos Químicos", "Vigencia según la actividad — vencimiento manual.", null, 23, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("50000000-0000-0000-0000-000000000009"), "Trabajador", true, null, null, false, false, true, "ADR", "Renovación cada 5 años (transporte de mercancías peligrosas).", null, 24, null, new Guid("00000000-0000-0000-0000-000000000001"), false, 60 },
                    { new Guid("5000000a-0000-0000-0000-00000000000a"), "Trabajador", false, null, null, false, false, true, "Soldadura", "Vigencia según política de cada empresa — vencimiento manual.", null, 25, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("5000000b-0000-0000-0000-00000000000b"), "Trabajador", false, null, null, false, false, true, "Operador de Puente Grúa", "Vigencia según política de cada empresa — vencimiento manual.", null, 26, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("5000000c-0000-0000-0000-00000000000c"), "Trabajador", false, null, null, false, false, true, "Operador de Grúa Torre", "Vigencia según normativa aplicable — vencimiento manual.", null, 27, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("5000000d-0000-0000-0000-00000000000d"), "Trabajador", false, null, null, false, false, true, "Operador de Grúa Móvil", "Vigencia según normativa aplicable — vencimiento manual.", null, 28, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("5000000e-0000-0000-0000-00000000000e"), "Trabajador", false, null, null, false, false, true, "Operador de Dumper", "Vigencia según política de cada empresa — vencimiento manual.", null, 29, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("5000000f-0000-0000-0000-00000000000f"), "Trabajador", false, null, null, false, false, true, "Operador de Retroexcavadora", "Vigencia según política de cada empresa — vencimiento manual.", null, 30, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("50000010-0000-0000-0000-000000000010"), "Trabajador", false, null, null, false, false, true, "Operador de Minicargadora", "Vigencia según política de cada empresa — vencimiento manual.", null, 31, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("50000011-0000-0000-0000-000000000011"), "Trabajador", false, null, null, false, false, true, "Operador de Manipulador Telescópico", "Vigencia según política de cada empresa — vencimiento manual.", null, 32, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("50000012-0000-0000-0000-000000000012"), "Trabajador", false, null, null, false, false, true, "Permiso de residencia", "Solo aplica a trabajadores extranjeros de fuera de la UE — vencimiento manual.", null, 33, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("50000013-0000-0000-0000-000000000013"), "Trabajador", false, null, null, false, false, true, "Permiso de trabajo", "Solo aplica a trabajadores extranjeros de fuera de la UE — vencimiento manual.", null, 34, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("50000014-0000-0000-0000-000000000014"), "Trabajador", false, null, null, false, false, true, "Certificado de Registro de Ciudadano de la UE", "Solo aplica a trabajadores extranjeros de la UE — vencimiento manual.", null, 35, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("50000015-0000-0000-0000-000000000015"), "Trabajador", false, null, null, false, false, true, "Certificado A1 de Seguridad Social", "Trabajadores desplazados temporalmente desde otro país de la UE — vigencia ligada a la duración del desplazamiento.", null, 36, null, new Guid("00000000-0000-0000-0000-000000000001"), false, null }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Alertas_DocumentoId",
                table: "Alertas",
                column: "DocumentoId");

            migrationBuilder.CreateIndex(
                name: "IX_Asignaciones_CentroId",
                table: "Asignaciones",
                column: "CentroId");

            migrationBuilder.CreateIndex(
                name: "IX_Asignaciones_TenantId_TrabajadorId_CentroId_FechaAlta",
                table: "Asignaciones",
                columns: new[] { "TenantId", "TrabajadorId", "CentroId", "FechaAlta" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AsignacionesOperadorDelegado_DelegacionTenantId_UsuarioId",
                table: "AsignacionesOperadorDelegado",
                columns: new[] { "DelegacionTenantId", "UsuarioId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AsignacionesOperadorDelegado_UsuarioId",
                table: "AsignacionesOperadorDelegado",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditoriasExtraccionIa_TenantId_CreadaEnUtc",
                table: "AuditoriasExtraccionIa",
                columns: new[] { "TenantId", "CreadaEnUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CanalesGestionDocumental_TenantId_CentroId",
                table: "CanalesGestionDocumental",
                columns: new[] { "TenantId", "CentroId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Centros_ClienteId",
                table: "Centros",
                column: "ClienteId");

            migrationBuilder.CreateIndex(
                name: "IX_Centros_EmpresaId",
                table: "Centros",
                column: "EmpresaId");

            migrationBuilder.CreateIndex(
                name: "IX_Clientes_EjecutivoUsuarioId",
                table: "Clientes",
                column: "EjecutivoUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_Clientes_RazonSocial",
                table: "Clientes",
                column: "RazonSocial");

            migrationBuilder.CreateIndex(
                name: "IX_Clientes_TenantId_Cif",
                table: "Clientes",
                columns: new[] { "TenantId", "Cif" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConfiguracionesIaDocumentoCliente_TenantId_ClienteId_TipoDo~",
                table: "ConfiguracionesIaDocumentoCliente",
                columns: new[] { "TenantId", "ClienteId", "TipoDocumentoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConversacionesCorreo_FechaUltimoMensajeUtc",
                table: "ConversacionesCorreo",
                column: "FechaUltimoMensajeUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ConversacionesCorreo_TenantId_ClienteId",
                table: "ConversacionesCorreo",
                columns: new[] { "TenantId", "ClienteId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversacionesCorreo_TenantId_Estado",
                table: "ConversacionesCorreo",
                columns: new[] { "TenantId", "Estado" });

            migrationBuilder.CreateIndex(
                name: "IX_CredencialesAccesoEmpresa_TenantId_EmpresaId",
                table: "CredencialesAccesoEmpresa",
                columns: new[] { "TenantId", "EmpresaId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CredencialesAccesoSubcontrata_TenantId_SubcontrataId",
                table: "CredencialesAccesoSubcontrata",
                columns: new[] { "TenantId", "SubcontrataId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DelegacionesTenant_TenantConsultoraId_TenantClienteId_Activa",
                table: "DelegacionesTenant",
                columns: new[] { "TenantConsultoraId", "TenantClienteId", "Activa" });

            migrationBuilder.CreateIndex(
                name: "IX_DeteccionesTrabajador_DocumentoId_Resuelta",
                table: "DeteccionesTrabajador",
                columns: new[] { "DocumentoId", "Resuelta" });

            migrationBuilder.CreateIndex(
                name: "IX_DeteccionesTrabajador_EmpresaId_Resuelta",
                table: "DeteccionesTrabajador",
                columns: new[] { "EmpresaId", "Resuelta" });

            migrationBuilder.CreateIndex(
                name: "IX_Documentos_ClienteId_TipoDocumentoId",
                table: "Documentos",
                columns: new[] { "ClienteId", "TipoDocumentoId" });

            migrationBuilder.CreateIndex(
                name: "IX_Documentos_EmpresaId_TipoDocumentoId",
                table: "Documentos",
                columns: new[] { "EmpresaId", "TipoDocumentoId" });

            migrationBuilder.CreateIndex(
                name: "IX_Documentos_FechaVencimiento",
                table: "Documentos",
                column: "FechaVencimiento");

            migrationBuilder.CreateIndex(
                name: "IX_Documentos_ProyectoId_TipoDocumentoId",
                table: "Documentos",
                columns: new[] { "ProyectoId", "TipoDocumentoId" });

            migrationBuilder.CreateIndex(
                name: "IX_Documentos_TrabajadorId_TipoDocumentoId",
                table: "Documentos",
                columns: new[] { "TrabajadorId", "TipoDocumentoId" });

            migrationBuilder.CreateIndex(
                name: "IX_Documentos_VehiculoId_TipoDocumentoId",
                table: "Documentos",
                columns: new[] { "VehiculoId", "TipoDocumentoId" });

            migrationBuilder.CreateIndex(
                name: "IX_Empresas_TenantId_Cif",
                table: "Empresas",
                columns: new[] { "TenantId", "Cif" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Empresas_TenantId_RazonSocial",
                table: "Empresas",
                columns: new[] { "TenantId", "RazonSocial" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmpresasClientes_ClienteId",
                table: "EmpresasClientes",
                column: "ClienteId");

            migrationBuilder.CreateIndex(
                name: "IX_EmpresasClientes_TenantId_EmpresaId_ClienteId",
                table: "EmpresasClientes",
                columns: new[] { "TenantId", "EmpresaId", "ClienteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Evaluaciones_CentroId",
                table: "Evaluaciones",
                column: "CentroId");

            migrationBuilder.CreateIndex(
                name: "IX_Evaluaciones_TrabajadorId",
                table: "Evaluaciones",
                column: "TrabajadorId");

            migrationBuilder.CreateIndex(
                name: "IX_ExtraccionesIaCache_TenantId_HashSha256",
                table: "ExtraccionesIaCache",
                columns: new[] { "TenantId", "HashSha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Incidencias_CentroId",
                table: "Incidencias",
                column: "CentroId");

            migrationBuilder.CreateIndex(
                name: "IX_Incidencias_TrabajadorId",
                table: "Incidencias",
                column: "TrabajadorId");

            migrationBuilder.CreateIndex(
                name: "IX_MacrosRespuesta_TenantId_ClienteId",
                table: "MacrosRespuesta",
                columns: new[] { "TenantId", "ClienteId" });

            migrationBuilder.CreateIndex(
                name: "IX_MensajesCorreo_ConversacionCorreoId",
                table: "MensajesCorreo",
                column: "ConversacionCorreoId");

            migrationBuilder.CreateIndex(
                name: "IX_MensajesCorreo_FechaUtc",
                table: "MensajesCorreo",
                column: "FechaUtc");

            migrationBuilder.CreateIndex(
                name: "IX_NotificacionesUsuario_UsuarioDestinatarioId_Leida",
                table: "NotificacionesUsuario",
                columns: new[] { "UsuarioDestinatarioId", "Leida" });

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantesConversacion_ConversacionCorreoId",
                table: "ParticipantesConversacion",
                column: "ConversacionCorreoId");

            migrationBuilder.CreateIndex(
                name: "IX_Proyectos_CentroId",
                table: "Proyectos",
                column: "CentroId");

            migrationBuilder.CreateIndex(
                name: "IX_Proyectos_TenantId_ClienteId_Nombre",
                table: "Proyectos",
                columns: new[] { "TenantId", "ClienteId", "Nombre" },
                unique: true,
                filter: "NOT \"EstaEliminado\"");

            migrationBuilder.CreateIndex(
                name: "IX_ProyectosTecnicos_TenantId_ProyectoId_TrabajadorId_FechaAlta",
                table: "ProyectosTecnicos",
                columns: new[] { "TenantId", "ProyectoId", "TrabajadorId", "FechaAlta" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProyectosTecnicos_TrabajadorId",
                table: "ProyectosTecnicos",
                column: "TrabajadorId");

            migrationBuilder.CreateIndex(
                name: "IX_RegistrosActividadSoporte_DelegacionTenantId_OcurridaEnUtc",
                table: "RegistrosActividadSoporte",
                columns: new[] { "DelegacionTenantId", "OcurridaEnUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RegistrosAuditoria_EntidadTipo_EntidadId",
                table: "RegistrosAuditoria",
                columns: new[] { "EntidadTipo", "EntidadId" });

            migrationBuilder.CreateIndex(
                name: "IX_RegistrosAuditoria_FechaUtc",
                table: "RegistrosAuditoria",
                column: "FechaUtc");

            migrationBuilder.CreateIndex(
                name: "IX_RequisitosDocumentales_CentroId",
                table: "RequisitosDocumentales",
                column: "CentroId");

            migrationBuilder.CreateIndex(
                name: "IX_RevisionesIaDocumento_DocumentoId_Resuelta",
                table: "RevisionesIaDocumento",
                columns: new[] { "DocumentoId", "Resuelta" });

            migrationBuilder.CreateIndex(
                name: "IX_SolicitudesPurga_TipoDato_Estado",
                table: "SolicitudesPurga",
                columns: new[] { "TipoDato", "Estado" });

            migrationBuilder.CreateIndex(
                name: "IX_Subcontratas_TenantId_RazonSocial",
                table: "Subcontratas",
                columns: new[] { "TenantId", "RazonSocial" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubcontratasClientes_ClienteId",
                table: "SubcontratasClientes",
                column: "ClienteId");

            migrationBuilder.CreateIndex(
                name: "IX_SubcontratasClientes_TenantId_SubcontrataId_ClienteId",
                table: "SubcontratasClientes",
                columns: new[] { "TenantId", "SubcontrataId", "ClienteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubcontratasEmpresas_EmpresaId",
                table: "SubcontratasEmpresas",
                column: "EmpresaId");

            migrationBuilder.CreateIndex(
                name: "IX_SubcontratasEmpresas_TenantId_SubcontrataId_EmpresaId",
                table: "SubcontratasEmpresas",
                columns: new[] { "TenantId", "SubcontrataId", "EmpresaId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TarifasCliente_ClienteId",
                table: "TarifasCliente",
                column: "ClienteId");

            migrationBuilder.CreateIndex(
                name: "IX_TarifasCliente_TenantId_ClienteId_Concepto",
                table: "TarifasCliente",
                columns: new[] { "TenantId", "ClienteId", "Concepto" },
                unique: true,
                filter: "NOT \"EstaEliminado\"");

            migrationBuilder.CreateIndex(
                name: "IX_TiposDocumento_TenantId_Nombre",
                table: "TiposDocumento",
                columns: new[] { "TenantId", "Nombre" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TiposDocumentoCentros_CentroId",
                table: "TiposDocumentoCentros",
                column: "CentroId");

            migrationBuilder.CreateIndex(
                name: "IX_TiposDocumentoCentros_TenantId_TipoDocumentoId_CentroId",
                table: "TiposDocumentoCentros",
                columns: new[] { "TenantId", "TipoDocumentoId", "CentroId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Trabajadores_EmpresaId",
                table: "Trabajadores",
                column: "EmpresaId");

            migrationBuilder.CreateIndex(
                name: "IX_Trabajadores_SubcontrataId",
                table: "Trabajadores",
                column: "SubcontrataId");

            migrationBuilder.CreateIndex(
                name: "IX_Trabajadores_TenantId_Dni",
                table: "Trabajadores",
                columns: new[] { "TenantId", "Dni" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Vehiculos_EmpresaId",
                table: "Vehiculos",
                column: "EmpresaId");

            migrationBuilder.CreateIndex(
                name: "IX_Vehiculos_SubcontrataId",
                table: "Vehiculos",
                column: "SubcontrataId");

            migrationBuilder.CreateIndex(
                name: "IX_Vehiculos_TenantId_NumeroPlaca",
                table: "Vehiculos",
                columns: new[] { "TenantId", "NumeroPlaca" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Visitas_CentroId",
                table: "Visitas",
                column: "CentroId");

            migrationBuilder.CreateIndex(
                name: "IX_Visitas_FechaFin",
                table: "Visitas",
                column: "FechaFin");

            migrationBuilder.CreateIndex(
                name: "IX_VisitasTrabajadores_TenantId_VisitaId_TrabajadorId",
                table: "VisitasTrabajadores",
                columns: new[] { "TenantId", "VisitaId", "TrabajadorId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VisitasTrabajadores_TrabajadorId",
                table: "VisitasTrabajadores",
                column: "TrabajadorId");

            migrationBuilder.CreateIndex(
                name: "IX_VisitasTrabajadores_VisitaId",
                table: "VisitasTrabajadores",
                column: "VisitaId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Alertas");

            migrationBuilder.DropTable(
                name: "AprobacionesDocumento");

            migrationBuilder.DropTable(
                name: "Asignaciones");

            migrationBuilder.DropTable(
                name: "AsignacionesOperadorDelegado");

            migrationBuilder.DropTable(
                name: "AspNetRoleClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "AuditoriasExtraccionIa");

            migrationBuilder.DropTable(
                name: "CanalesGestionDocumental");

            migrationBuilder.DropTable(
                name: "Centros");

            migrationBuilder.DropTable(
                name: "Clientes");

            migrationBuilder.DropTable(
                name: "ConfiguracionesIaDocumentoCliente");

            migrationBuilder.DropTable(
                name: "CredencialesAccesoEmpresa");

            migrationBuilder.DropTable(
                name: "CredencialesAccesoSubcontrata");

            migrationBuilder.DropTable(
                name: "DelegacionesTenant");

            migrationBuilder.DropTable(
                name: "DeteccionesTrabajador");

            migrationBuilder.DropTable(
                name: "Documentos");

            migrationBuilder.DropTable(
                name: "Empresas");

            migrationBuilder.DropTable(
                name: "EmpresasClientes");

            migrationBuilder.DropTable(
                name: "Evaluaciones");

            migrationBuilder.DropTable(
                name: "ExtraccionesIaCache");

            migrationBuilder.DropTable(
                name: "Incidencias");

            migrationBuilder.DropTable(
                name: "MacrosRespuesta");

            migrationBuilder.DropTable(
                name: "MensajesCorreo");

            migrationBuilder.DropTable(
                name: "NotificacionesUsuario");

            migrationBuilder.DropTable(
                name: "ParametrosSistema");

            migrationBuilder.DropTable(
                name: "ParticipantesConversacion");

            migrationBuilder.DropTable(
                name: "Proyectos");

            migrationBuilder.DropTable(
                name: "ProyectosTecnicos");

            migrationBuilder.DropTable(
                name: "RegistrosActividadSoporte");

            migrationBuilder.DropTable(
                name: "RegistrosAuditoria");

            migrationBuilder.DropTable(
                name: "RequisitosDocumentales");

            migrationBuilder.DropTable(
                name: "RevisionesIaDocumento");

            migrationBuilder.DropTable(
                name: "SolicitudesPurga");

            migrationBuilder.DropTable(
                name: "Subcontratas");

            migrationBuilder.DropTable(
                name: "SubcontratasClientes");

            migrationBuilder.DropTable(
                name: "SubcontratasEmpresas");

            migrationBuilder.DropTable(
                name: "TarifasCliente");

            migrationBuilder.DropTable(
                name: "Tenants");

            migrationBuilder.DropTable(
                name: "TiposDocumento");

            migrationBuilder.DropTable(
                name: "TiposDocumentoCentros");

            migrationBuilder.DropTable(
                name: "Trabajadores");

            migrationBuilder.DropTable(
                name: "Vehiculos");

            migrationBuilder.DropTable(
                name: "Visitas");

            migrationBuilder.DropTable(
                name: "VisitasTrabajadores");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "ConversacionesCorreo");
        }
    }
}
