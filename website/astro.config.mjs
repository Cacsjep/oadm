// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

// GitHub Pages project site: https://cacsjep.github.io/oadm/
export default defineConfig({
	site: 'https://cacsjep.github.io',
	base: '/oadm',
	integrations: [
		starlight({
			title: 'OADM',
			description: 'Open source alternative to AXIS Device Manager for Windows, Linux and macOS.',
			logo: { src: './src/assets/logo.png', alt: 'OADM' },
			favicon: '/favicon.png',
			customCss: ['./src/styles/oadm.css'],
			head: [{ tag: 'script', attrs: { src: '/oadm/lightbox.js', defer: true } }],
			social: [
				{ icon: 'github', label: 'GitHub', href: 'https://github.com/Cacsjep/oadm' },
				{ icon: 'linkedin', label: 'LinkedIn', href: 'https://www.linkedin.com/in/christoph-acs-70150795/' },
			],
			editLink: { baseUrl: 'https://github.com/Cacsjep/oadm/edit/main/website/' },
			lastUpdated: false,
			components: {
				ThemeProvider: './src/components/DarkTheme.astro',
				ThemeSelect: './src/components/NoThemeSelect.astro',
				SiteTitle: './src/components/BackHome.astro',
			},
			sidebar: [
				{
					label: 'Get started',
					items: [
						{ label: 'Install', slug: 'start/install' },
						{ label: 'First start', slug: 'start/first-start' },
					],
				},
				{
					label: 'Devices',
					items: [
						{ label: 'Add devices', slug: 'devices/add' },
						{ label: 'Devices page', slug: 'devices/devices-page' },
						{ label: 'Live view', slug: 'devices/live-view' },
						{ label: 'Tags', slug: 'devices/tags' },
						{ label: 'Tasks', slug: 'devices/tasks' },
					],
				},
				{
					label: 'Device tasks',
					items: [
						{ label: 'Overview', slug: 'tasks/overview' },
						{ label: 'Upgrade firmware', slug: 'tasks/firmware' },
						{ label: 'Date and time', slug: 'tasks/date-and-time' },
						{ label: 'Network settings', slug: 'tasks/network' },
						{ label: 'Users', slug: 'tasks/users' },
						{ label: 'Applications (ACAP)', slug: 'tasks/applications' },
						{ label: 'Certificates', slug: 'tasks/certificates' },
					],
				},
				{
					label: 'Tools',
					items: [
						{ label: 'Snapshot report', slug: 'tools/snapshot-report' },
						{ label: 'System report', slug: 'tools/system-report' },
						{ label: 'VAPIX Commander', slug: 'tools/vapix-commander' },
						{ label: 'Hardening scan', slug: 'tools/hardening-scan' },
						{ label: 'Metadata Monitor', slug: 'tools/metadata-monitor' },
						{ label: 'PKI', slug: 'tools/pki' },
						{ label: 'NTP server', slug: 'tools/ntp-server' },
						{ label: 'DHCP server', slug: 'tools/dhcp-server' },
					],
				},
				{
					label: 'Administration',
					items: [
						{ label: 'Users and roles', slug: 'admin/users' },
						{ label: 'Settings', slug: 'admin/settings' },
						{ label: 'Ports, data and backup', slug: 'admin/ports-and-data' },
						{ label: 'Logs and troubleshooting', slug: 'admin/troubleshooting' },
					],
				},
				{
					label: 'Developers',
					items: [
						{ label: 'Build from source', slug: 'dev/build' },
						{ label: 'Write a plugin', slug: 'dev/plugins' },
						{ label: 'Build a plugin with AI', slug: 'dev/ai-plugins' },
					],
				},
				{
					label: 'Project',
					items: [
						{ label: 'FAQ', slug: 'project/faq' },
						{ label: 'Security', slug: 'project/security' },
						{ label: 'License', slug: 'project/license' },
					],
				},
			],
		}),
	],
});
