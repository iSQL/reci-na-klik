<script lang="ts">
	import * as Avatar from '$lib/components/ui/avatar';
	import type { User } from '$lib/types';
	import * as m from '$lib/paraglide/messages';

	interface Props {
		user: User | null | undefined;
	}

	let { user }: Props = $props();


	// Computed display name
	const displayName = $derived.by(() => {
		const first = user?.firstName;
		const last = user?.lastName;
		if (first || last) {
			return [first, last].filter(Boolean).join(' ');
		}
		return user?.username ?? m.common_user();
	});


	// Computed initials for avatar
	const initials = $derived.by(() => {
		const first = user?.firstName;
		const last = user?.lastName;
		if (first && last) {
			return `${first[0]}${last[0]}`.toUpperCase();
		}
		if (first) {
			return first.substring(0, 2).toUpperCase();
		}
		return user?.username?.substring(0, 2).toUpperCase() ?? 'ME';
	});
</script>

<div class="flex flex-col items-center gap-4 sm:flex-row">
	<div class="h-24 w-24 rounded-full">
		<Avatar.Root class="h-24 w-24 ring-2 ring-border">
			<Avatar.Fallback class="text-lg">{initials}</Avatar.Fallback>
		</Avatar.Root>
	</div>
	<div class="flex flex-col gap-1 text-center sm:text-start">
		<h3 class="text-lg font-medium">{displayName}</h3>
		<p class="text-sm text-muted-foreground">{user?.email ?? ''}</p>
	</div>
</div>
