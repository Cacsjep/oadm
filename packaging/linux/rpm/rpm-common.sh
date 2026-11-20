# Shared by build-rpm.sh and build-site-node-rpm.sh (sourced, never run): the RPM version and architecture of a RID and
# build_rpm, which turns a staged file tree into one package with rpmbuild (the rpm package of Debian/Ubuntu, Fedora or
# RHEL). The files are the published single-file apps: no automatic dependencies, no stripping, no debug package.

# rpm_setup <rid> <version>: sets RPM_ARCH (x86_64, aarch64) and RPM_VERSION (a pre-release sorts first: 1.2.0~rc.1).
rpm_setup() {
  case "$1" in
    linux-x64) RPM_ARCH=x86_64 ;;
    linux-arm64) RPM_ARCH=aarch64 ;;
    *) echo "error: unsupported RID '$1' (linux-x64 or linux-arm64)" >&2; exit 2 ;;
  esac
  RPM_VERSION="$(printf '%s' "${2#v}" | sed 's/-/~/')"
  command -v rpmbuild >/dev/null 2>&1 || { echo "error: rpmbuild not found (package rpm on Debian and Ubuntu, rpm-build on Fedora)" >&2; exit 1; }
}

# The .NET runtime needs glibc, libgcc, libstdc++, zlib, OpenSSL (HTTPS) and ICU (globalization): package names of Fedora and RHEL.
RPM_RUNTIME_REQUIRES="glibc, libgcc, libstdc++, libz.so.1()(64bit), openssl-libs, libicu"
# Avalonia on X11 needs fontconfig and the X11 libraries.
RPM_DESKTOP_REQUIRES="fontconfig, libX11, libICE, libSM, libXrandr, libXi, libXcursor"

# rpm_scriptlet <section> <file>: the section with the file's content (CR removed), nothing when there is no file.
rpm_scriptlet() {
  [ -f "$2" ] || return 0
  printf '\n%s\n' "$1"
  tr -d '\r' < "$2"
}

# build_rpm <name> <root> <summary> <header> <description> <files> <scripts> <out-dir>
#   root     staged tree (its paths are the installed paths), modes already set
#   header   extra spec lines (Requires, Recommends, ...)
#   files    the %files lines (a directory includes everything below it)
#   scripts  prefix of the scriptlet files: <scripts>.pre, .post, .preun, .postun (each optional)
# Writes <out-dir>/<name>-<version>-1.<arch>.rpm.
build_rpm() {
  local name="$1" root="$2" summary="$3" header="$4" description="$5" files="$6" scripts="$7" out="$8"
  local top spec
  top="$(mktemp -d)"
  spec="$top/$name.spec"
  {
    cat <<EOF
%global debug_package %{nil}
%global __os_install_post %{nil}
%global __arch_install_post %{nil}
%global _build_id_links none
%define _binary_payload w6.xzdio
%define _rpmfilename %%{NAME}-%%{VERSION}-%%{RELEASE}.%%{ARCH}.rpm

Name: $name
Version: $RPM_VERSION
Release: 1
Summary: $summary
License: Apache-2.0
URL: https://oadm.app
Vendor: OADM contributors
Packager: OADM contributors <oadm@acs-dev.org>
AutoReqProv: no
$header

%description
$description

%install
cp -a "$root/." "%{buildroot}/"

%files
%defattr(-,root,root,-)
$files
EOF
    rpm_scriptlet %pre "$scripts.pre"
    rpm_scriptlet %post "$scripts.post"
    rpm_scriptlet %preun "$scripts.preun"
    rpm_scriptlet %postun "$scripts.postun"
  } > "$spec"
  mkdir -p "$top/BUILD" "$top/BUILDROOT" "$top/RPMS" "$top/SOURCES" "$top/SPECS" "$top/SRPMS"
  rpmbuild -bb --quiet --target "$RPM_ARCH" --define "_topdir $top" "$spec"
  mkdir -p "$out"
  mv "$top/RPMS/$name-$RPM_VERSION-1.$RPM_ARCH.rpm" "$out/"
  rm -rf "$top"
  echo "built $out/$name-$RPM_VERSION-1.$RPM_ARCH.rpm"
}

# rpm_permissions <root> <executable>...: directories 0755, files 0644, the executables 0755.
rpm_permissions() {
  local root="$1" x
  shift
  find "$root" -type d -exec chmod 0755 {} +
  find "$root" -type f -exec chmod 0644 {} +
  for x in "$@"; do chmod 0755 "$root/$x"; done
}

# rpm_docs <root> <package> <from> <file>...: /usr/share/doc/<package> with the license texts of the publish folder.
rpm_docs() {
  local root="$1" package="$2" from="$3" f
  shift 3
  mkdir -p "$root/usr/share/doc/$package"
  for f in "$@"; do cp "$from/$f" "$root/usr/share/doc/$package/"; done
}

# rpm_icons <root> <icon name> <repo root>: the app icon in every size of /icon into the hicolor theme.
rpm_icons() {
  local root="$1" icon="$2" repo="$3" n
  for n in 16 24 32 48 64 128 256 512; do
    mkdir -p "$root/usr/share/icons/hicolor/${n}x${n}/apps"
    cp "$repo/icon/oadm-app-icon-$n.png" "$root/usr/share/icons/hicolor/${n}x${n}/apps/$icon.png"
  done
}
