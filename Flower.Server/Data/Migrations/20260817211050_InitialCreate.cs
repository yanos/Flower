using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flower.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Playlists",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Comment = table.Column<string>(type: "TEXT", nullable: true),
                    Public = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Playlists", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tracks",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Artist = table.Column<string>(type: "TEXT", nullable: true),
                    AlbumArtist = table.Column<string>(type: "TEXT", nullable: true),
                    Album = table.Column<string>(type: "TEXT", nullable: true),
                    ArtistId = table.Column<string>(type: "TEXT", nullable: false),
                    AlbumId = table.Column<string>(type: "TEXT", nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    Genre = table.Column<string>(type: "TEXT", nullable: true),
                    TrackNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    DiscNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: false),
                    Bitrate = table.Column<int>(type: "INTEGER", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    Suffix = table.Column<string>(type: "TEXT", nullable: true),
                    ContentType = table.Column<string>(type: "TEXT", nullable: true),
                    DateAdded = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    PlayCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Starred = table.Column<bool>(type: "INTEGER", nullable: false),
                    StarredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tracks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlaylistTracks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PlaylistId = table.Column<string>(type: "TEXT", nullable: false),
                    TrackId = table.Column<string>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaylistTracks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlaylistTracks_Playlists_PlaylistId",
                        column: x => x.PlaylistId,
                        principalTable: "Playlists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistTracks_PlaylistId_Position",
                table: "PlaylistTracks",
                columns: new[] { "PlaylistId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_AlbumId",
                table: "Tracks",
                column: "AlbumId");

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_ArtistId",
                table: "Tracks",
                column: "ArtistId");

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_Path",
                table: "Tracks",
                column: "Path",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlaylistTracks");

            migrationBuilder.DropTable(
                name: "Tracks");

            migrationBuilder.DropTable(
                name: "Playlists");
        }
    }
}
